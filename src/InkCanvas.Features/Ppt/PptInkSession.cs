using InkCanvas.Core.Diagnostics;
using InkCanvas.Core.Ink;

namespace InkCanvas.Features.Ppt;

/// <summary>
/// Coordinates per-slide ink and user-facing hooks around <see cref="PptInteropService"/>.
/// Construct/use on the UI dispatcher so callbacks can safely bridge the host ink controller.
/// This service never owns the app window or controller.
/// </summary>
public sealed class PptInkSession : IDisposable
{
    private const int SlideCacheCapacity = 8;

    private readonly PptInteropService _interop;
    private readonly IPptInkStore _store;
    private readonly Dictionary<int, List<InkStroke>> _slides = new();
    private readonly Dictionary<int, int> _slideIndexes = new();
    private readonly Dictionary<int, int?> _deferredSlides = new();
    private readonly SlideInkCache _inkCache = new(SlideCacheCapacity);
    private readonly SlideSwitchGate _prefetchGate = new();
    private readonly SemaphoreSlim _backgroundReads = new(2, 2);
    private PptSnapshot _last = PptSnapshot.Detached;
    private PptPresentationInfo _loadedPresentation;
    private PptInkArchive _mappingArchive;
    private CancellationTokenSource _detailsLoading;
    private string _noticeIdentity;
    private bool _showActive;
    private bool _disposed;
    private int? _lastSlideIndex;
    private int? _lastSlideId;
    private int? _lastShowPosition;
    private bool _legacyFilesDetected;
    private bool _offerInkMappingReview;
    private string _inkMappingSummary;
    private string _backupDirectory;
    private DateTime _suppressTransitionScreenshotUntilUtc;

    public PptInkSession(PptInteropService interop, IPptInkStore store = null)
    {
        _interop = interop ?? throw new ArgumentNullException(nameof(interop));
        _store = store;
        _interop.Failed += OnInteropFailed;
    }

    public Func<IReadOnlyList<InkStroke>> CaptureCurrentInk { get; set; } = () => Array.Empty<InkStroke>();
    public Func<bool> InkManagedExternally { get; set; } = () => false;
    public Action<IReadOnlyList<InkStroke>> ApplyCurrentInk { get; set; }
    public Func<int> MinimumScreenshotStrokeCount { get; set; } = () => 0;
    public Func<bool> AutoSavePresentationInk { get; set; } = () => true;
    public Func<bool> AutoSavePptScreenshots { get; set; } = () => false;
    public Func<bool> NotifyPreviousPage { get; set; } = () => false;
    public Func<bool> NotifyHiddenSlides { get; set; } = () => true;
    public Func<bool> NotifySlideTimings { get; set; } = () => true;
    public Func<string> AutoSaveRoot { get; set; } = () => string.Empty;
    public Func<bool> UseScreenshotDateFolders { get; set; } = () => false;
    public Func<bool> SaveInkWithScreenshot { get; set; } = () => false;

    /// <summary>
    /// Optional per-slide loader used for cache misses and neighbor prefetch. Defaults to the
    /// file-backed store when one is available; hosts may override it for tests or other stores.
    /// </summary>
    public Func<int, IReadOnlyList<InkStroke>> LoadSlide { get; set; }

    /// <summary>Diagnostics for the neighbor cache.</summary>
    public SlideInkCache InkCache => _inkCache;

    public PptSnapshot Snapshot => _last;
    public IReadOnlyList<InkStroke> CurrentStrokes => Clone(CaptureCurrentInk());

    /// <summary>Capture one slide for preview without copying the rest of the presentation.</summary>
    public IReadOnlyList<InkStroke> CaptureSlideInk(int slideId)
    {
        ThrowIfDisposed();
        if (slideId <= 0) return Array.Empty<InkStroke>();
        if (_last.IsSlideShowRunning && _last.Slide?.SlideId == slideId) return CurrentStrokes;
        if (_slides.TryGetValue(slideId, out var strokes)) return Clone(strokes);
        return _deferredSlides.ContainsKey(slideId) ? TryLoadSlide(slideId) ?? Array.Empty<InkStroke>() : Array.Empty<InkStroke>();
    }

    public Task<IReadOnlyList<InkStroke>> CaptureSlideInkAsync(int slideId, CancellationToken token)
    {
        ThrowIfDisposed();
        token.ThrowIfCancellationRequested();
        if (!_deferredSlides.ContainsKey(slideId) || _slides.ContainsKey(slideId) || _last.Slide?.SlideId == slideId)
            return Task.FromResult(CaptureSlideInk(slideId));
        var presentation = _loadedPresentation;
        return Task.Run<IReadOnlyList<InkStroke>>(async () =>
        {
            await _backgroundReads.WaitAsync(token);
            try { return (_store as PptInkFileStore)?.TryLoadSlide(presentation, slideId) ?? Array.Empty<InkStroke>(); }
            finally { _backgroundReads.Release(); }
        }, token);
    }

    /// <summary>
    /// Events request immediate refreshes. Polling covers missed events and hosts that
    /// cannot expose the Office connection point.
    /// </summary>
    public TimeSpan RecommendedPollInterval =>
        _last.IsSlideShowRunning && (!_interop.EventsConnected || _last.IsWindowedShow)
            ? TimeSpan.FromMilliseconds(80) : TimeSpan.FromMilliseconds(900);

    public event Action<PptInkTransition> InkTransition;
    public event Action<PptPresentationNotice> PresentationNotice;
    public event Action<PptScreenshotRequest> ScreenshotRequested;
    public event Action<PptFailure> Failed;

    /// <summary>Attach opportunistically, poll COM, and orchestrate ink changes.</summary>
    public PptSnapshot Poll()
    {
        ThrowIfDisposed();
        if (!_interop.IsAttached) _interop.TryAttach();
        PptSnapshot current = _interop.Poll();
        if (SameSlideState(_last, current))
        {
            if (DateTime.UtcNow > _suppressTransitionScreenshotUntilUtc) _suppressTransitionScreenshotUntilUtc = DateTime.MinValue;
            _last = current;
            return current;
        }
        ProcessTransition(current);
        return current;
    }

    public void RequestScreenshot()
    {
        ThrowIfDisposed();
        if (!_last.IsSlideShowRunning || _last.Slide is null) return;
        RaiseScreenshot(PptScreenshotReason.Explicit);
    }

    public bool RestoreHiddenSlides() => _interop.RestoreHiddenSlides();
    public bool DisableSlideTimings() => _interop.DisableSlideTimings();
    public bool ShowSlideNavigationPane() => _interop.ShowSlideNavigationPane();

    public PptInkArchive CreateArchive()
    {
        ThrowIfDisposed();
        if (_loadedPresentation is null) return null;
        CaptureOutgoing(_last);
        if (_last.IsSlideShowRunning && _last.Slide is not null)
        {
            _lastSlideIndex = _last.Slide.Index > 0 ? _last.Slide.Index : _lastSlideIndex;
            _lastSlideId = _last.Slide.SlideId > 0 ? _last.Slide.SlideId : _lastSlideId;
            _lastShowPosition = _last.ShowPosition > 0 ? _last.ShowPosition : _lastShowPosition;
        }
        return BuildArchive();
    }

    public PptInkArchive CreateIndexArchive()
    {
        ThrowIfDisposed();
        if (_loadedPresentation is null) return null;
        CaptureOutgoing(_last);
        return new(_loadedPresentation, _lastSlideIndex, _lastShowPosition,
            _slides.Select(pair => new PptStoredSlide(pair.Key, _slideIndexes.GetValueOrDefault(pair.Key), pair.Value))
                .Concat(_deferredSlides.Where(pair => !_slides.ContainsKey(pair.Key)).Select(pair => new PptStoredSlide(pair.Key,
                    _slideIndexes.GetValueOrDefault(pair.Key), Array.Empty<InkStroke>(), true, pair.Value))).ToArray());
    }

    public void Save()
    {
        ThrowIfDisposed();
        if (_store is null || _loadedPresentation is null || !AutoSavePresentationInk()) return;
        CaptureOutgoing(_last);
        if (_last.IsSlideShowRunning && _last.Slide is not null)
        {
            _lastSlideIndex = _last.Slide.Index > 0 ? _last.Slide.Index : _lastSlideIndex;
            _lastSlideId = _last.Slide.SlideId > 0 ? _last.Slide.SlideId : _lastSlideId;
            _lastShowPosition = _last.ShowPosition > 0 ? _last.ShowPosition : _lastShowPosition;
        }
        Persist();
    }

    /// <summary>
    /// Drops the in-memory ink for the loaded presentation and persists the empty archive.
    /// Callers must back up the presentation directory first; this never runs implicitly.
    /// </summary>
    public void ClearPresentationInk()
    {
        ThrowIfDisposed();
        foreach (int slideId in _slides.Keys.ToArray()) _slides[slideId] = new();
        foreach (int slideId in _deferredSlides.Keys) _slides[slideId] = new();
        _deferredSlides.Clear();
        if (_last.IsSlideShowRunning && _last.Slide is { SlideId: > 0, Index: > 0 } slide)
        {
            _slides[slide.SlideId] = new();
            _slideIndexes[slide.SlideId] = slide.Index;
        }
        _inkCache.Clear();
        _prefetchGate.BeginSession();
        _lastSlideIndex = null;
        _lastSlideId = null;
        _offerInkMappingReview = false;
        _inkMappingSummary = null;
        if (_store is not null && _loadedPresentation is not null)
        {
            try { _store.Save(BuildArchive()); }
            catch (Exception ex) { Failed?.Invoke(new PptFailure("Clear presentation ink", ex.Message, 0)); }
        }
        ApplyCurrentInk?.Invoke(Array.Empty<InkStroke>());
    }

    public bool RunSlideShow(PptSlideShowStart start = PptSlideShowStart.PresentationSettings)
    { ThrowIfDisposed(); return _interop.RunSlideShow(start); }
    public bool Next() => NavigateWithScreenshot(PptScreenshotReason.BeforeNavigation, _interop.Next);
    public bool Previous() => NavigateWithScreenshot(PptScreenshotReason.BeforeNavigation, _interop.Previous);
    public bool GoToSlide(int oneBasedIndex) =>
        NavigateWithScreenshot(PptScreenshotReason.BeforeNavigation, () => _interop.GoToSlide(oneBasedIndex));

    public bool GoToSlideId(int slideId) =>
        NavigateWithScreenshot(PptScreenshotReason.BeforeNavigation, () => _interop.GoToSlideId(slideId));
    public bool ExitSlideShow() { ThrowIfDisposed(); return _interop.ExitSlideShow(); }

    private bool NavigateWithScreenshot(PptScreenshotReason reason, Func<bool> navigate)
    {
        ThrowIfDisposed();
        if (_last.IsSlideShowRunning && AutoSavePptScreenshots() && ShouldSaveScreenshot())
        {
            RaiseScreenshot(reason);
            bool moved = navigate();
            if (moved) _suppressTransitionScreenshotUntilUtc = DateTime.UtcNow.AddSeconds(5);
            return moved;
        }
        return navigate();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Save();
        _detailsLoading?.Cancel();
        _interop.Failed -= OnInteropFailed;
        _disposed = true;
    }

    private void ProcessTransition(PptSnapshot current)
    {
        PptSnapshot previous = _last;
        bool wasShow = _showActive;
        bool isShow = current.IsSlideShowRunning;
        bool samePresentation = SamePresentation(_loadedPresentation, current.Presentation);
        bool newPresentation = current.IsAttached && !samePresentation;

        if (_showActive && (!isShow || newPresentation))
        {
            CaptureOutgoing(previous);
            // Any in-flight neighbor prefetch belongs to the outgoing show/slide.
            _prefetchGate.BeginSession();
            if (AutoSavePresentationInk() && previous.IsSlideShowRunning && previous.Slide is not null)
            {
                _lastSlideIndex = previous.Slide.Index > 0 ? previous.Slide.Index : null;
                _lastSlideId = previous.Slide.SlideId > 0 ? previous.Slide.SlideId : null;
                _lastShowPosition = previous.ShowPosition > 0 ? previous.ShowPosition : null;
            }
            Persist();
            if (newPresentation || !isShow)
            {
                if (!InkManagedExternally()) ApplyCurrentInk?.Invoke(Array.Empty<InkStroke>());
                RaiseTransition(PptInkTransitionKind.End, previous, current, Array.Empty<InkStroke>());
                _showActive = false;
            }
        }

        if (newPresentation)
        {
            _detailsLoading?.Cancel();
            _mappingArchive = null;
            _loadedPresentation = current.Presentation;
            _slides.Clear();
            _deferredSlides.Clear();
            _slideIndexes.Clear();
            _inkCache.Clear();
            _prefetchGate.BeginSession();
            _noticeIdentity = null;
            _lastSlideIndex = null;
            _lastSlideId = null;
            _lastShowPosition = null;
            _legacyFilesDetected = false;
            _offerInkMappingReview = false;
            _inkMappingSummary = null;
            _backupDirectory = null;
            if (_store is not null)
            {
                try
                {
                    PptInkArchive archive = _store is PptInkFileStore files ? files.LoadForSession(current.Presentation) : _store.Load(current.Presentation);
                    archive = MigrateLegacyArchive(archive, current.Presentation);
                    if (archive is not null)
                    {
                        if (AutoSavePresentationInk())
                        {
                            foreach (var slide in archive.Slides ?? Array.Empty<PptStoredSlide>())
                            {
                                if (slide.IsDeferred) _deferredSlides[slide.SlideId] = slide.StoredStrokeCount;
                                else _slides[slide.SlideId] = Clone(slide.Strokes).ToList();
                                if (slide.SlideIndex > 0 && !_slideIndexes.ContainsKey(slide.SlideId))
                                    _slideIndexes[slide.SlideId] = slide.SlideIndex;
                            }
                        }
                        _lastSlideIndex = archive.LastSlideIndex;
                        _lastSlideId = archive.LastSlideId;
                        _lastShowPosition = archive.LastShowPosition;
                        _legacyFilesDetected = archive.LegacyInkFilesDetected;
                        if (current.Presentation.SlideCount > 200) _mappingArchive = archive;
                        else EvaluateMapping(archive);
                    }
                }
                catch (Exception ex) { Failed?.Invoke(new PptFailure("Load presentation ink", ex.Message, 0)); }
            }
        }

        if (isShow)
        {
            bool began = !_showActive || newPresentation;
            bool slideChanged = previous.IsSlideShowRunning && isShow &&
                (previous.Slide?.SlideId != current.Slide?.SlideId || !SamePresentation(previous.Presentation, current.Presentation));
            if (began)
            {
                _showActive = true;
                _prefetchGate.BeginSession();
                // MW_PPT.cs:402 resets the per-show state; a leftover navigation suppression
                // from the previous show must not eat the first external-change screenshot.
                _suppressTransitionScreenshotUntilUtc = DateTime.MinValue;
                ApplySlide(current, previous, PptInkTransitionKind.Begin);
                OfferPresentationNotice(current);
            }
            else if (slideChanged)
            {
                bool suppressDuplicateScreenshot = DateTime.UtcNow <= _suppressTransitionScreenshotUntilUtc;
                _suppressTransitionScreenshotUntilUtc = DateTime.MinValue;
                if (!suppressDuplicateScreenshot && AutoSavePptScreenshots() && ShouldSaveScreenshot())
                    RaiseScreenshot(PptScreenshotReason.ExternalSlideChange, current);
                ApplySlide(current, previous, PptInkTransitionKind.Slide);
            }
        }
        else if (newPresentation && !wasShow)
        {
            OfferPresentationNotice(current);
        }

        _last = current;
    }

    private readonly System.Diagnostics.Stopwatch _applyWatch = new();
    public long LastSlideApplyMs { get; private set; }
    public long MaxSlideApplyMs { get; private set; }
    public int SlideApplyOver50MsCount { get; private set; }

    private void ApplySlide(PptSnapshot current, PptSnapshot previous, PptInkTransitionKind kind)
    {
        _applyWatch.Restart();
        try
        {
            CaptureOutgoing(previous);
            List<InkStroke> incoming = ResolveSlideInk(current.Slide);
            _prefetchGate.NextGeneration();
if (!InkManagedExternally()) ApplyCurrentInk?.Invoke(Clone(incoming));
            if (current.Slide is not null && current.Slide.Index > 0)
                _slideIndexes[current.Slide.SlideId] = current.Slide.Index;
            _lastSlideIndex = current.Slide?.Index > 0 ? current.Slide.Index : _lastSlideIndex;
            _lastSlideId = current.Slide?.SlideId > 0 ? current.Slide.SlideId : _lastSlideId;
            _lastShowPosition = current.ShowPosition > 0 ? current.ShowPosition : _lastShowPosition;
            RaiseTransition(kind, previous, current, incoming);
            PrefetchNeighbors(current.Slide);
        }
        finally
        {
            _applyWatch.Stop();
            LastSlideApplyMs = _applyWatch.ElapsedMilliseconds;
            if (LastSlideApplyMs > MaxSlideApplyMs) MaxSlideApplyMs = LastSlideApplyMs;
            if (LastSlideApplyMs > 50)
            {
                SlideApplyOver50MsCount++;
                Core.Diagnostics.FileLog.WriteLogToFile(
                    $"PPT slide apply took {LastSlideApplyMs}ms（>50ms）", Core.Diagnostics.LogType.Trace);
            }
        }
    }

    /// <summary>
    /// Cache → in-memory archive → single-slide load. Cache reads are detached clones that
    /// can be passed directly to the ink controller.
    /// </summary>
    private List<InkStroke> ResolveSlideInk(PptSlideInfo slide)
    {
        if (slide is null || slide.SlideId <= 0) return new List<InkStroke>();

        if (_inkCache.Get(slide.SlideId) is { } cached) return cached.ToList();

        if (_slides.TryGetValue(slide.SlideId, out var found))
        {
            List<InkStroke> copy = Clone(found).ToList();
            _inkCache.Set(slide.SlideId, copy);
            return copy;
        }

        if (AutoSavePresentationInk() && TryLoadSlide(slide.SlideId) is { Count: > 0 } loaded)
        {
            List<InkStroke> copy = loaded.ToList();
            _inkCache.Set(slide.SlideId, copy);
            return copy;
        }

        return new List<InkStroke>();
    }

    private IReadOnlyList<InkStroke> TryLoadSlide(int slideId)
    {
        if (slideId <= 0 || _loadedPresentation is null) return null;
        try
        {
            if (LoadSlide is not null) return LoadSlide(slideId);
            return _store is PptInkFileStore fileStore ? fileStore.TryLoadSlide(_loadedPresentation, slideId) : null;
        }
        catch (Exception ex)
        {
            FileLog.WriteLogToFile($"PPT slide load failed (slideId {slideId}): {ex}", LogType.Info);
            return null;
        }
    }

    /// <summary>
    /// Warms the cache for the slides before/after the current one. Work runs on the thread
    /// pool and only lands while the captured (session, generation) is still current, so a
    /// fast page flip can never overwrite the new page's ink with a stale neighbor.
    /// </summary>
    private void PrefetchNeighbors(PptSlideInfo slide)
    {
        if (_disposed || slide is null || slide.Index <= 0 || _loadedPresentation is null) return;
        if (!AutoSavePresentationInk()) return;

        int session = _prefetchGate.Session;
        int generation = _prefetchGate.Generation;
        PptPresentationInfo presentation = _loadedPresentation;
        Func<int, IReadOnlyList<InkStroke>> loader = LoadSlide;
        PptInkFileStore fileStore = _store as PptInkFileStore;

        foreach (int index in new[] { slide.Index - 1, slide.Index + 1 })
        {
            if (index <= 0) continue;
            int slideId = FindSlideId(index);
            if (slideId <= 0 || _inkCache.Contains(slideId) || _slides.ContainsKey(slideId)) continue;

            _ = Task.Run(() =>
            {
                if (!_backgroundReads.Wait(0)) return;
                try
                {
                    if (_disposed || !_prefetchGate.IsCurrent(session, generation)) return;
                    IReadOnlyList<InkStroke> strokes = loader is not null
                        ? loader(slideId)
                        : fileStore?.TryLoadSlide(presentation, slideId);
                    if (strokes is null || strokes.Count == 0) return;
                    if (_disposed) return;
                    if (!_prefetchGate.IsCurrent(session, generation)) return;
                    if (!ReferenceEquals(presentation, _loadedPresentation)) return;
                    _inkCache.Set(slideId, strokes);
                }
                catch (Exception ex)
                {
                    FileLog.WriteLogToFile($"PPT prefetch failed (slideId {slideId}): {ex}", LogType.Info);
                }
                finally { _backgroundReads.Release(); }
            });
        }
    }

    private int FindSlideId(int index)
    {
        foreach (KeyValuePair<int, int> pair in _slideIndexes)
        {
            if (pair.Value == index) return pair.Key;
        }
        return 0;
    }

    private void CaptureOutgoing(PptSnapshot snapshot)
    {
        if (InkManagedExternally()) return;
        if (snapshot?.IsSlideShowRunning != true || snapshot.Slide is null || snapshot.Presentation is null || !_showActive) return;
        if (snapshot.Slide.Index > 0) _slideIndexes[snapshot.Slide.SlideId] = snapshot.Slide.Index;
        if (snapshot.Slide.SlideId > 0) _lastSlideId = snapshot.Slide.SlideId;
        try
        {
            List<InkStroke> strokes = Clone(CaptureCurrentInk()).ToList();
            _slides[snapshot.Slide.SlideId] = strokes;
            _inkCache.Set(snapshot.Slide.SlideId, strokes);
        }
        catch (Exception ex) { Failed?.Invoke(new PptFailure("Capture outgoing slide ink", ex.Message, 0)); }
    }

    private void RaiseTransition(PptInkTransitionKind kind, PptSnapshot previous, PptSnapshot current,
        IReadOnlyList<InkStroke> incoming)
    {
        if (InkTransition is null) return;
        IReadOnlyList<InkStroke> outgoing = Array.Empty<InkStroke>();
        if (previous?.Slide is not null && _slides.TryGetValue(previous.Slide.SlideId, out var found)) outgoing = Clone(found);
        InkTransition?.Invoke(new PptInkTransition(kind, previous, current, outgoing, Clone(incoming)));
    }

    private void OfferPresentationNotice(PptSnapshot snapshot, PptPresentationDetails details = null)
    {
        if (!snapshot.IsAttached || details is null && _noticeIdentity == snapshot.Presentation?.Identity) return;
        bool previousEnabled = NotifyPreviousPage();
        if (!previousEnabled && !NotifyHiddenSlides() && !NotifySlideTimings() && !_legacyFilesDetected && !_offerInkMappingReview
            && _mappingArchive?.Slides.Count is not > 0) return;
        if (details is null && snapshot.Presentation.SlideCount > 200)
        {
            _noticeIdentity = snapshot.Presentation.Identity;
            _ = LoadLargePresentationDetailsAsync(snapshot);
            return;
        }
        details ??= _interop.GetPresentationDetails();
        if (details is null) return;
        RefreshSlideIndexes(details);
        if (_mappingArchive is { } archive)
        {
            EvaluateMapping(archive, details);
            _mappingArchive = null;
        }
        _noticeIdentity = snapshot.Presentation?.Identity;
        int? resume = null;
        bool degraded = false;
        if (previousEnabled)
        {
            resume = ResolveResumeIndex(_lastSlideId, _lastSlideIndex, details.Slides, out degraded);
            if (degraded)
                FileLog.WriteLogToFile(
                    $"PPT resume fell back to page {_lastSlideIndex} (slideId {_lastSlideId} unavailable).",
                    LogType.Info);
        }
        bool hidden = NotifyHiddenSlides() && details.HasHiddenSlides;
        bool timings = NotifySlideTimings() && details.HasSlideTimings;
        PresentationNotice?.Invoke(new PptPresentationNotice(details, resume, hidden, timings, _legacyFilesDetected,
            degraded, _offerInkMappingReview, _inkMappingSummary, _backupDirectory));
    }

    private async Task LoadLargePresentationDetailsAsync(PptSnapshot snapshot)
    {
        using var loading = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _detailsLoading = loading;
        try
        {
            var details = await _interop.GetPresentationDetailsAsync(loading.Token);
            if (_disposed || loading.IsCancellationRequested || !ReferenceEquals(_detailsLoading, loading)
                || _last.Presentation?.Identity != snapshot.Presentation.Identity) return;
            if (details is not null) OfferPresentationNotice(snapshot, details);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { FileLog.WriteLogToFile($"PPT background details skipped: {ex.Message}", LogType.Info); }
        finally { if (ReferenceEquals(_detailsLoading, loading)) _detailsLoading = null; }
    }

    /// <summary>
    /// Prefers the stored SlideId and falls back to the legacy page index when that id is
    /// missing from the live deck. <paramref name="degraded"/> is true whenever the page
    /// number was used, so the host can explain the downgrade.
    /// </summary>
    public static int? ResolveResumeIndex(int? lastSlideId, int? lastSlideIndex,
        IReadOnlyList<PptSlideInfo> slides, out bool degraded)
    {
        degraded = false;
        IReadOnlyList<PptSlideInfo> list = slides ?? Array.Empty<PptSlideInfo>();
        if (lastSlideId is > 0)
        {
            foreach (PptSlideInfo slide in list)
            {
                if (slide is not null && slide.SlideId == lastSlideId.Value && slide.Index > 0)
                    return slide.Index;
            }
        }
        if (lastSlideIndex is > 0 && list.Any(s => s is not null && s.Index == lastSlideIndex.Value))
        {
            degraded = true;
            return lastSlideIndex;
        }
        return null;
    }

    private bool ShouldSaveScreenshot()
    {
        try { return CaptureCurrentInk().Count > Math.Max(0, MinimumScreenshotStrokeCount()); }
        catch { return false; }
    }

    private void RaiseScreenshot(PptScreenshotReason reason, PptSnapshot snapshot = null)
    {
        snapshot ??= _last;
        if (snapshot.Slide is null || snapshot.Presentation is null) return;
        string path = null;
        string root = AutoSaveRoot() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(root))
        {
            string folder = Path.Combine(root, "Auto Saved - PPT Screenshots");
            if (UseScreenshotDateFolders()) folder = Path.Combine(folder, DateTime.Now.ToString("yyyy-MM-dd"));
            string presentationPart = SafeFilePart(snapshot.Presentation.Name);
            int position = snapshot.ShowPosition > 0 ? snapshot.ShowPosition : snapshot.Slide.Index;
            int filePosition = reason == PptScreenshotReason.ExternalSlideChange && _last.ShowPosition > 0
                ? _last.ShowPosition : position;
            string imageName = reason == PptScreenshotReason.Explicit
                ? $"{position}_{DateTime.Now:HH-mm-ss}"
                : filePosition.ToString("0");
            path = Path.Combine(folder, presentationPart, imageName + ".png");
        }
        PptSlideInfo inkSlide = reason == PptScreenshotReason.ExternalSlideChange ? _last.Slide : snapshot.Slide;
        ScreenshotRequested?.Invoke(new PptScreenshotRequest(snapshot, inkSlide, Clone(CaptureCurrentInk()), reason,
            path, SaveInkWithScreenshot()));
    }

    private void Persist()
    {
        if (InkManagedExternally()) return;
        if (_store is null || _loadedPresentation is null || !AutoSavePresentationInk()) return;
        try { _store.Save(BuildArchive(includeDeferred: false)); }
        catch (Exception ex) { Failed?.Invoke(new PptFailure("Save presentation ink", ex.Message, 0)); }
    }

    private PptInkArchive BuildArchive(bool includeDeferred = true) => new(_loadedPresentation, _lastSlideIndex, _lastShowPosition,
        _slides.Select(pair => new PptStoredSlide(pair.Key,
            _slideIndexes.TryGetValue(pair.Key, out int index) ? index : 0, Clone(pair.Value)))
            .Concat(includeDeferred ? _deferredSlides.Keys.Where(id => !_slides.ContainsKey(id)).Select(id => new PptStoredSlide(id,
                _slideIndexes.GetValueOrDefault(id), TryLoadSlide(id) ?? Array.Empty<InkStroke>())) : Array.Empty<PptStoredSlide>()).ToList(),
        _legacyFilesDetected, _lastSlideId, PptInkFileStore.CurrentManifestVersion);

    /// <summary>
    /// Runs the one-way legacy upgrade for file-backed stores, then reloads so the in-memory
    /// archive reflects the new v2 manifest. Any failure is logged and the original archive is
    /// returned unchanged.
    /// </summary>
    private PptInkArchive MigrateLegacyArchive(PptInkArchive archive, PptPresentationInfo presentation)
    {
        if (archive is null || _store is not PptInkFileStore fileStore || !AutoSavePresentationInk()) return archive;
        if (archive.ManifestVersion >= PptInkFileStore.CurrentManifestVersion || archive.Slides.Count == 0 && !archive.LegacyInkFilesDetected) return archive;
        try
        {
            PptPresentationDetails details = _interop.GetPresentationDetails();
            string directory = fileStore.GetPresentationDirectory(presentation);
            SlideInkMigrationReport report = SlideInkMigrator.MigrateIfNeeded(archive, details, directory);
            if (!report.Migrated) return archive;

            _backupDirectory = report.BackupDirectory;
            return _store.Load(presentation) ?? archive;
        }
        catch (Exception ex)
        {
            FileLog.WriteLogToFile($"PPT ink migration skipped: {ex}", LogType.Error);
            return archive;
        }
    }

    private void EvaluateMapping(PptInkArchive archive, PptPresentationDetails details = null)
    {
        if (archive.Slides.Count == 0) return;
        try
        {
            details ??= _interop.GetPresentationDetails();
            if (details is null) return;
            SlideMappingIssue issue = SlideMappingInspector.Inspect(archive, details.Slides);
            int stored = archive.Slides?.Count ?? 0;
            _offerInkMappingReview = issue.NeedsWarning;
            if (!issue.NeedsWarning) return;
            int missing = issue.MissingSlideIds?.Count ?? 0;
            _inkMappingSummary = $"存档 {stored} 页，当前 {details.Slides.Count} 页" +
                (missing > 0 ? $"，{missing} 页在课件中已找不到" : string.Empty) +
                (issue.PageOrderChanged ? "，页序已变化" : string.Empty) +
                (issue.CountChanged ? "，页数不一致" : string.Empty) + "。";
        }
        catch (Exception ex)
        {
            FileLog.WriteLogToFile($"PPT ink mapping inspection failed: {ex}", LogType.Error);
        }
    }

    private void RefreshSlideIndexes(PptPresentationDetails details)
    {
        if (details is null) return;
        foreach (PptSlideInfo slide in details.Slides)
        {
            if (slide.SlideId > 0 && slide.Index > 0) _slideIndexes[slide.SlideId] = slide.Index;
        }
    }

    private static bool SamePresentation(PptPresentationInfo a, PptPresentationInfo b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (string.Equals(a.Identity, b.Identity, StringComparison.Ordinal)) return true;
        if (a.HasSavedPath && b.HasSavedPath && !string.IsNullOrWhiteSpace(a.FullName) && !string.IsNullOrWhiteSpace(b.FullName))
            return string.Equals(a.FullName, b.FullName, StringComparison.OrdinalIgnoreCase);
        return !a.HasSavedPath && !b.HasSavedPath && a.SlideCount == b.SlideCount &&
            string.Equals(a.Name, b.Name, StringComparison.Ordinal) && a.SlideWidth == b.SlideWidth && a.SlideHeight == b.SlideHeight;
    }

    private static bool SameSlideState(PptSnapshot a, PptSnapshot b) =>
        a.State == b.State && SamePresentation(a.Presentation, b.Presentation) &&
        a.Slide?.SlideId == b.Slide?.SlideId && a.Slide?.Index == b.Slide?.Index &&
        a.ShowPosition == b.ShowPosition && a.WindowHandle == b.WindowHandle;

    private static IReadOnlyList<InkStroke> Clone(IEnumerable<InkStroke> strokes) =>
        strokes?.Where(s => s is not null).Select(s => s.Clone()).ToList() ?? new List<InkStroke>();

    private static string SafeFilePart(string value)
    {
        string result = string.IsNullOrWhiteSpace(value) ? "Presentation" : value;
        foreach (char c in Path.GetInvalidFileNameChars()) result = result.Replace(c, '_');
        return result.Replace('/', '_').Replace('\\', '_');
    }

    private void OnInteropFailed(PptFailure failure) => Failed?.Invoke(failure);

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PptInkSession));
    }
}
