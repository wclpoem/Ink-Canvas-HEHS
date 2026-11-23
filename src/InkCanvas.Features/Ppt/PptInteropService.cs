using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace InkCanvas.Features.Ppt;

/// <summary>Late-bound PowerPoint/WPS COM adapter. Construct and use on one WinUI STA thread.</summary>
/// <remarks>COM events request a refresh on the owning dispatcher. Periodic polling remains
/// available for hosts without events. Snapshots contain only managed data.</remarks>
public sealed class PptInteropService : IDisposable
{
    [DllImport("ole32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int CLSIDFromProgID(string progId, out Guid clsid);
    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, out IntPtr punk);

    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly Guid _attachmentId = Guid.NewGuid();
    private object _app;
    private string _progId;
    private PptSnapshot _snapshot = PptSnapshot.Detached;
    private bool _disposed;
    private static readonly Guid ApplicationEvents = new("914934C2-5A91-11CF-8700-00AA0060263B");
    private readonly List<(int Id, Delegate Handler)> _events = new();
    public bool EventsConnected => _events.Count > 0;
    public event Action RefreshRequested;
    // Per-presentation SlideID->index map so slideshow polls do not re-scan every slide
    // through COM on each tick (ink-switch latency). Cleared when identity/count changes.
    private readonly Dictionary<int, int> _slideIndexById = new();
    private string _slideIndexKey;
    // FullName/path/page-setup are stable for one COM presentation identity; reuse them
    // across polls (Name is re-checked live) instead of re-reading every property per tick.
    private PptPresentationInfo _cachedInfo;
    private PptPresentationDetails _cachedDetails;
    private long _detailsExpireAt;
    private nint _presentationIdentity;

    public string[] ProgIds { get; set; } = { "PowerPoint.Application", "KWPP.Application" };
    public bool AllowWps { get; set; } = true;
    public bool IsAttached => _app is not null;
    public string AttachedProgId => _progId;
    public PptSnapshot Snapshot { get { VerifyThread(); return _snapshot; } }
    public PptFailure LastFailure { get; private set; }
    public event Action<PptSnapshot> SnapshotChanged;
    public event Action<PptFailure> Failed;

    /// <summary>Compatibility surface for the existing shell polling loop.</summary>
    public bool PollSlideShowState(ref bool lastKnown)
    {
        PptSnapshot snapshot = Poll();
        bool current = snapshot.IsSlideShowRunning;
        if (current == lastKnown) return false;
        lastKnown = current;
        return true;
    }

    public bool IsSlideShowRunning() => Poll().IsSlideShowRunning;
    public int CurrentSlideIndex()
    {
        PptSnapshot snapshot = Poll();
        return snapshot.IsSlideShowRunning ? snapshot.ShowPosition : 0;
    }
    public int SlideCount()
    {
        PptSnapshot snapshot = Poll();
        return snapshot.IsSlideShowRunning ? snapshot.Presentation?.SlideCount ?? 0 : 0;
    }

    public bool TryAttach()
    {
        VerifyThread();
        if (_app is not null) { Poll(); return true; }
        // MW_PPT.cs:95-98: with WPS support off and wpp.exe running, WPF refuses to attach at
        // all, because WPS can own the PowerPoint.Application running-object-table entry.
        if (!AllowWps && IsWpsProcessRunning())
        {
            Publish(PptSnapshot.Detached);
            return false;
        }
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            Fail("Attach", "COM attachment must run on the owning STA/UI thread.", unchecked((int)0x8001010E));
            return false;
        }
        foreach (string progId in ProgIds ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(progId) || (!AllowWps && progId.StartsWith("KWPP", StringComparison.OrdinalIgnoreCase))) continue;
            IntPtr punk = IntPtr.Zero;
            try
            {
                if (CLSIDFromProgID(progId, out Guid clsid) != 0) continue;
                if (GetActiveObject(ref clsid, IntPtr.Zero, out punk) != 0 || punk == IntPtr.Zero) continue;
                object app = Marshal.GetObjectForIUnknown(punk);
                ReleasePointer(punk);
                punk = IntPtr.Zero;
                _app = app;
                _progId = progId;
                ConnectEvents();
                LastFailure = null;
                Poll();
                if (_app is not null) return true;
            }
            catch (Exception ex)
            {
                ReleasePointer(punk);
                Fail("Attach", ex.Message, Marshal.GetHRForException(ex));
            }
        }
        Publish(PptSnapshot.Detached);
        return false;
    }

    private static bool IsWpsProcessRunning()
    {
        try
        {
            Process[] processes = Process.GetProcessesByName("wpp");
            bool any = processes.Length > 0;
            foreach (Process process in processes) process.Dispose();
            return any;
        }
        catch { return false; }
    }

    /// <summary>Refreshes presentation, show and active-slide state; call on the owning UI STA.</summary>
    public PptSnapshot Poll()
    {
        VerifyThread();
        if (_app is null) return Publish(PptSnapshot.Detached);
        try
        {
            var result = CaptureSnapshot();
            LastFailure = null;
            return Publish(result);
        }
        catch (Exception ex)
        {
            int hr = Marshal.GetHRForException(ex);
            Fail("Poll", ex.Message, hr);
            if (IsDisconnectedError(hr))
            {
                DisconnectEvents();
                ReleaseComObject(_app);
                _app = null;
                _progId = null;
                _slideIndexById.Clear();
                _slideIndexKey = null;
                _cachedInfo = null;
                _cachedDetails = null;
                ReleasePresentationIdentity();
                return Publish(PptSnapshot.Detached);
            }
            return _snapshot;
        }
    }

    public PptPresentationDetails GetPresentationDetails() => ReadPresentationDetailsAsync(false, default).GetAwaiter().GetResult();

    public Task<PptPresentationDetails> GetPresentationDetailsAsync(CancellationToken token) => ReadPresentationDetailsAsync(true, token);

    private async Task<PptPresentationDetails> ReadPresentationDetailsAsync(bool yieldBetweenBatches, CancellationToken token)
    {
        VerifyThread();
        if (_app is null || !_snapshot.IsAttached) return null;
        if (_cachedDetails?.Presentation.Identity == _snapshot.Presentation?.Identity
            && _cachedDetails.Presentation.SlideCount == _snapshot.Presentation.SlideCount
            && Environment.TickCount64 < _detailsExpireAt) return _cachedDetails;
        var info = _snapshot.Presentation;
        if (yieldBetweenBatches) await Task.Delay(1, token);
        using var scope = new ComScope();
        try
        {
            token.ThrowIfCancellationRequested();
            VerifyThread();
            if (_snapshot.Presentation?.Identity != info.Identity) return null;
            dynamic app = _app;
            object p = _snapshot.IsSlideShowRunning ? GetShowPresentation(scope, app) : GetActivePresentation(scope, app);
            if (p is null) return null;
            dynamic presentation = p;
            dynamic slides = scope.Own((object)presentation.Slides);
            var list = new List<PptSlideInfo>();
            int count = (int)slides.Count;
            var batch = Stopwatch.StartNew();
            for (int index = 1; index <= count; index++)
            {
                token.ThrowIfCancellationRequested();
                if (_snapshot.Presentation?.Identity != info.Identity || _snapshot.Presentation.SlideCount != count) return null;
                using var slideScope = new ComScope();
                dynamic slide = slideScope.Own((object)slides[index]);
                int id = (int)slide.SlideID;
                if (id > 0) _slideIndexById[id] = index;
                dynamic transition = SafeCom(slideScope, () => (object)slide.SlideShowTransition);
                list.Add(new PptSlideInfo(id, index, SafeString(() => (string)slide.Name),
                    transition is null ? null : SafeTriState(() => (int)transition.Hidden),
                    transition is null ? null : SafeTriState(() => (int)transition.AdvanceOnTime),
                    transition is null ? null : SafeDouble(() => (double)transition.AdvanceTime)));
                if (yieldBetweenBatches && batch.ElapsedMilliseconds >= 6)
                {
                    await Task.Delay(1, token);
                    VerifyThread();
                    batch.Restart();
                }
            }
            dynamic settings = scope.Own((object)presentation.SlideShowSettings);
            _cachedDetails = new PptPresentationDetails(info, list,
                SafeInt(() => (int)settings.AdvanceMode));
            _detailsExpireAt = Environment.TickCount64 + 2000;
            return _cachedDetails;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Fail("Read presentation details", ex.Message, Marshal.GetHRForException(ex)); return null; }
    }

    /// <summary>读取指定幻灯片（按 SlideID）中所有形状的文本；失败时返回空串，不影响翻页与批注。</summary>
    public string GetSlideText(int slideId)
    {
        VerifyThread();
        if (_app is null || !_snapshot.IsAttached) return "";
        using var scope = new ComScope();
        try
        {
            dynamic app = _app;
            object p = _snapshot.IsSlideShowRunning
                ? GetShowPresentation(scope, app)
                : GetActivePresentation(scope, app);
            if (p is null) return "";

            dynamic presentation = p;
            dynamic slides = scope.Own((object)presentation.Slides);
            dynamic slide = scope.Own(slideId > 0 ? (object)slides.FindBySlideID(slideId) : (object)slides[1]);

            if (slide is null) return "";

            dynamic shapes = scope.Own((object)slide.Shapes);
            int shapeCount = (int)shapes.Count;
            var builder = new StringBuilder();
            for (int index = 1; index <= shapeCount; index++)
            {
                dynamic shape = scope.Own((object)shapes[index]);
                string text = SafeString(() =>
                {
                    dynamic frame = shape.TextFrame;
                    return (int)frame.HasText == -1 ? (string)frame.TextRange.Text : "";
                });
                if (!string.IsNullOrWhiteSpace(text)) builder.AppendLine(text.Trim());
            }

            return builder.ToString().Trim();
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// 当前页的动画/点击阶段。仅放映中可读（SlideShowView.GetClickCount）；
    /// 不可用时返回 0，由调用方安全降级，不影响翻页与批注。
    /// </summary>
    public int GetAnimationStep()
    {
        VerifyThread();
        if (_app is null || !_snapshot.IsAttached || !_snapshot.IsSlideShowRunning) return 0;
        using var scope = new ComScope();
        try
        {
            dynamic app = _app;
            dynamic window = GetShowWindow(scope, app);
            if (window is null) return 0;
            dynamic view = scope.Own((object)window.View);
            return SafeInt(() => (int)view.GetClickCount()) ?? 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// 翻页/退出/黑屏时暂停课件内音频与视频，避免后台继续播放。
    /// 嵌入对象或 COM 不支持时静默降级，不影响翻页与批注。
    /// </summary>
    public int PauseMedia()
    {
        VerifyThread();
        if (_app is null || !_snapshot.IsAttached) return 0;
        using var scope = new ComScope();
        try
        {
            dynamic app = _app;
            object p = _snapshot.IsSlideShowRunning
                ? GetShowPresentation(scope, app)
                : GetActivePresentation(scope, app);
            if (p is null) return 0;

            dynamic slides = scope.Own((object)((dynamic)p).Slides);
            int count = Math.Min(200, (int)slides.Count);
            int paused = 0;
            for (int i = 1; i <= count; i++)
            {
                dynamic slide = scope.Own((object)slides[i]);
                dynamic shapes = scope.Own((object)slide.Shapes);
                int shapeCount = Math.Min(500, (int)shapes.Count);
                for (int j = 1; j <= shapeCount; j++)
                {
                    dynamic shape = scope.Own((object)shapes[j]);
                    int mediaType = SafeInt(() => (int)shape.MediaType) ?? -1;
                    if (mediaType != 2 && mediaType != 3) continue;
                    try
                    {
                        shape.MediaFormat.Pause();
                        paused++;
                    }
                    catch (Exception)
                    {
                        // 单个嵌入对象不支持 MediaFormat.Pause：跳过，继续处理其余媒体。
                    }
                }
            }

            return paused;
        }
        catch (Exception)
        {
            return 0;
        }
    }

public bool Next() => InvokeShow("Next", view => view.Next(), activateWindow: true);
    public bool Previous() => InvokeShow("Previous", view => view.Previous(), activateWindow: true);
    public bool ExitSlideShow() => InvokeShow("Exit slideshow", view => view.Exit());

    /// <summary>Show PowerPoint's built-in slide navigation pane, as the original page-number control did.</summary>
    public bool ShowSlideNavigationPane()
        => InvokeShow("Show slide navigation pane", view => view.SlideNavigation.Visible = true, activateWindow: true);

    /// <summary>Export a slide without changing the current editing or slideshow page.</summary>
    public bool ExportSlidePreview(PptPresentationInfo requested, int slideId, string path, int maximumDimension = 512)
    {
        VerifyThread();
        if (_app is null || slideId <= 0 || requested is null) return false;
        using var scope = new ComScope();
        try
        {
            Poll();
            if (!MatchesPresentation(requested, _snapshot.Presentation)) return false;
            dynamic app = _app;
            object p = _snapshot.IsSlideShowRunning ? GetShowPresentation(scope, app) : GetActivePresentation(scope, app);
            if (p is null) return false;
            dynamic slides = scope.Own((object)((dynamic)p).Slides);
            dynamic slide = scope.Own((object)slides.FindBySlideID(slideId));
            double width = Math.Max(1, requested.SlideWidth), height = Math.Max(1, requested.SlideHeight);
            double scale = Math.Clamp(maximumDimension, 64, 1024) / Math.Max(width, height);
            slide.Export(Path.GetFullPath(path), "PNG", Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
            return File.Exists(path);
        }
        catch (Exception ex) { Fail("Export slide preview", ex.Message, Marshal.GetHRForException(ex)); return false; }
    }

    public IReadOnlyList<PptSlideInfo> GetPreviewSlides(PptPresentationInfo requested, CancellationToken token)
    {
        VerifyThread();
        Poll();
        if (_app is null || !MatchesPresentation(requested, _snapshot.Presentation))
            throw new InvalidOperationException("当前课件已切换，请重新打开预览。");
        using var scope = new ComScope();
        dynamic app = _app;
        object p = _snapshot.IsSlideShowRunning ? GetShowPresentation(scope, app) : GetActivePresentation(scope, app);
        if (p is null) throw new InvalidOperationException("尚未连接到当前课件。");
        dynamic slides = scope.Own((object)((dynamic)p).Slides);
        int count = (int)slides.Count;
        var result = new List<PptSlideInfo>(count);
        for (int index = 1; index <= count; index++)
        {
            token.ThrowIfCancellationRequested();
            using var itemScope = new ComScope();
            dynamic slide = itemScope.Own((object)slides[index]);
            result.Add(new((int)slide.SlideID, index, "", null, null, null));
        }
        return result;
    }

    public bool GoToSlideId(int slideId)
    {
        VerifyThread();
        if (_app is null || slideId <= 0) return false;
        using var scope = new ComScope();
        try
        {
            dynamic app = _app;
            object p = _snapshot.IsSlideShowRunning ? GetShowPresentation(scope, app) : GetActivePresentation(scope, app);
            if (p is null) return false;
            dynamic slides = scope.Own((object)((dynamic)p).Slides);
            dynamic slide = scope.Own((object)slides.FindBySlideID(slideId));
            return GoToSlide((int)slide.SlideIndex);
        }
        catch (Exception ex) { Fail("Go to slide ID", ex.Message, Marshal.GetHRForException(ex)); return false; }
    }

    public PptSlideInfo GetPreviewSlide(PptPresentationInfo requested, int index)
    {
        VerifyThread();
        Poll();
        if (_app is null || !MatchesPresentation(requested, _snapshot.Presentation))
            throw new InvalidOperationException("当前课件已切换，请重新打开预览。");
        using var scope = new ComScope();
        dynamic app = _app;
        object p = _snapshot.IsSlideShowRunning ? GetShowPresentation(scope, app) : GetActivePresentation(scope, app);
        if (p is null) throw new InvalidOperationException("尚未连接到当前课件。");
        dynamic slides = scope.Own((object)((dynamic)p).Slides);
        dynamic slide = scope.Own((object)slides[index]);
        return new((int)slide.SlideID, index, "", null, null, null);
    }

    internal static bool MatchesPresentation(PptPresentationInfo requested, PptPresentationInfo current) => requested is not null && current is not null
        && string.Equals(requested.ApplicationProgId, current.ApplicationProgId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(requested.FullName, current.FullName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(requested.Name, current.Name, StringComparison.Ordinal);

    /// <summary>Navigate by one-based presentation slide index, not SlideID/show position.</summary>
    public bool GoToSlide(int slideIndex)
    {
        VerifyThread();
        if (slideIndex < 1 || _app is null) return false;
        using var scope = new ComScope();
        try
        {
            dynamic app = _app;
            if (_snapshot.IsSlideShowRunning)
            {
                dynamic window = GetShowWindow(scope, app);
                if (window is null) return false;
                dynamic view = scope.Own((object)window.View);
                view.GotoSlide(slideIndex);
            }
            else
            {
                // MW_PPT.cs:155: outside a show the resume prompt jumps through
                // presentation.Windows[1].View; ActiveWindow is only the fallback.
                dynamic view = null;
                object p = GetActivePresentation(scope, app);
                if (p is not null)
                {
                    try
                    {
                        dynamic windows = scope.Own((object)((dynamic)p).Windows);
                        if ((int)windows.Count >= 1)
                        {
                            dynamic window = scope.Own((object)windows[1]);
                            view = scope.Own((object)window.View);
                        }
                    }
                    catch { /* Fall back to the active window below. */ }
                }
                if (view is null)
                {
                    dynamic window = scope.Own((object)app.ActiveWindow);
                    view = scope.Own((object)window.View);
                }
                view.GotoSlide(slideIndex);
            }
            LastFailure = null;
            return true;
        }
        catch (Exception ex) { Fail("Go to slide", ex.Message, Marshal.GetHRForException(ex)); return false; }
    }

    /// <summary>F5/settings, beginning, or Shift+F5/current; restores temporary show-range mutations.</summary>
    public bool RunSlideShow(PptSlideShowStart start = PptSlideShowStart.PresentationSettings)
    {
        VerifyThread();
        if (_app is null) return false;
        using var scope = new ComScope();
        try
        {
            dynamic app = _app;
            object p = GetActivePresentation(scope, app);
            if (p is null) return false;
            dynamic presentation = p;
            dynamic settings = scope.Own((object)presentation.SlideShowSettings);
            if (start == PptSlideShowStart.FromCurrentSlide && TryRunCurrentSlideCommand(scope, app))
            {
                LastFailure = null;
                return true;
            }
            if (start == PptSlideShowStart.PresentationSettings)
            {
                settings.Run();
            }
            else
            {
                int oldRange = (int)settings.RangeType;
                int oldStart = (int)settings.StartingSlide;
                int oldEnd = (int)settings.EndingSlide;
                try
                {
                    if (start == PptSlideShowStart.FromBeginning)
                    {
                        settings.RangeType = 1; // ppShowAll (F5)
                    }
                    else
                    {
                        int current = GetEditingSlideIndex(scope, app);
                        if (current < 1) return false;
                        dynamic slides = scope.Own((object)presentation.Slides);
                        settings.RangeType = 2; // ppShowSlideRange (WPS fallback for Shift+F5)
                        settings.StartingSlide = current;
                        settings.EndingSlide = (int)slides.Count;
                    }
                    settings.Run();
                }
                finally
                {
                    settings.RangeType = oldRange;
                    settings.StartingSlide = oldStart;
                    settings.EndingSlide = oldEnd;
                }
            }
            LastFailure = null;
            return true;
        }
        catch (Exception ex) { Fail("Run slideshow", ex.Message, Marshal.GetHRForException(ex)); return false; }
    }

    /// <summary>Reveal hidden slides, matching the positive action of the legacy prompt.</summary>
    public bool RestoreHiddenSlides()
    {
        VerifyThread();
        using var scope = new ComScope();
        try
        {
            if (_app is null) return false;
            dynamic app = _app;
            object p = _snapshot.IsSlideShowRunning ? GetShowPresentation(scope, app) : GetActivePresentation(scope, app);
            if (p is null) return false;
            dynamic slides = scope.Own((object)((dynamic)p).Slides);
            for (int i = 1; i <= (int)slides.Count; i++)
            {
                dynamic slide = scope.Own((object)slides[i]);
                dynamic transition = scope.Own((object)slide.SlideShowTransition);
                if ((int)transition.Hidden == -1) transition.Hidden = 0;
            }
            LastFailure = null;
            return true;
        }
        catch (Exception ex) { Fail("Restore hidden slides", ex.Message, Marshal.GetHRForException(ex)); return false; }
    }

    /// <summary>Set manual advance, matching the positive action of the legacy timing prompt.</summary>
    public bool DisableSlideTimings()
    {
        VerifyThread();
        using var scope = new ComScope();
        try
        {
            if (_app is null) return false;
            dynamic app = _app;
            object p = _snapshot.IsSlideShowRunning ? GetShowPresentation(scope, app) : GetActivePresentation(scope, app);
            if (p is null) return false;
            dynamic settings = scope.Own((object)((dynamic)p).SlideShowSettings);
            settings.AdvanceMode = 1; // ppSlideShowManualAdvance
            LastFailure = null;
            return true;
        }
        catch (Exception ex) { Fail("Disable slide timings", ex.Message, Marshal.GetHRForException(ex)); return false; }
    }

    public void Detach()
    {
        VerifyThread();
        DisconnectEvents();
        ReleaseComObject(_app);
        _app = null;
        _progId = null;
        _slideIndexById.Clear();
        _slideIndexKey = null;
        _cachedInfo = null;
        _cachedDetails = null;
        ReleasePresentationIdentity();
        LastFailure = null;
        Publish(PptSnapshot.Detached);
    }

    public void Dispose()
    {
        if (_disposed) return;
        VerifyThread();
        Detach();
        _disposed = true;
    }

    private PptSnapshot CaptureSnapshot()
    {
        using var scope = new ComScope();
        dynamic app = _app;
        object showWindow = GetShowWindow(scope, app);
        bool inShow = showWindow is not null;
        object p = inShow ? GetPresentation(scope, (dynamic)showWindow) : GetActivePresentation(scope, app);
        if (inShow && p is null) return _snapshot;
        if (p is null) { ReleasePresentationIdentity(); return PptSnapshot.Detached; }
        dynamic presentation = p;
        dynamic slides = scope.Own((object)presentation.Slides);
        int count = (int)slides.Count;
        IntPtr punk = Marshal.GetIUnknownForObject(p);
        // Keep the current COM identity alive between polls. Releasing the last reference
        // let Office return a new proxy each time and made an unchanged page look new.
        if (_presentationIdentity == punk) Marshal.Release(punk);
        else
        {
            if (_presentationIdentity != 0) Marshal.Release(_presentationIdentity);
            _presentationIdentity = punk;
        }
        string identity = $"{_attachmentId:N}:{_progId}:{punk.ToInt64():X}";
        // Name stays live (one call) so an in-place Save As invalidates the cached metadata.
        string name = SafeString(() => (string)presentation.Name) ?? string.Empty;
        PptPresentationInfo info;
        if (_cachedInfo is not null && _cachedInfo.Identity == identity && _cachedInfo.SlideCount == count &&
            string.Equals(_cachedInfo.Name, name, StringComparison.Ordinal))
        {
            info = _cachedInfo;
        }
        else
        {
            string fullName = SafeString(() => (string)presentation.FullName) ?? string.Empty;
            bool hasPath = !string.IsNullOrWhiteSpace(SafeString(() => (string)presentation.Path));
            dynamic pageSetup = scope.Own((object)presentation.PageSetup);
            double width = SafeDouble(() => (double)pageSetup.SlideWidth) ?? 0;
            double height = SafeDouble(() => (double)pageSetup.SlideHeight) ?? 0;
            info = new PptPresentationInfo(identity, name, fullName, hasPath, count, width, height, _progId);
            _cachedInfo = info;
        }
        string indexKey = $"{identity}:{count}";
        if (!string.Equals(indexKey, _slideIndexKey, StringComparison.Ordinal))
        {
            _slideIndexById.Clear();
            _slideIndexKey = indexKey;
        }
        PptSlideInfo current = null;
        nint hwnd = 0;
        int showPosition = 0;
        int? showState = null;
        bool windowedShow = false;
        if (inShow)
        {
            dynamic window = showWindow;
            hwnd = SafeNint(() => (nint)(long)window.HWND) ?? 0;
            windowedShow = SafeTriState(() => (int)window.IsFullScreen) == false;
            if (hwnd == 0)
                hwnd = _snapshot.IsSlideShowRunning && _snapshot.Presentation?.Identity == identity
                    && _snapshot.IsWindowedShow == windowedShow && Platform.Interop.NativeMethods.IsWindowVisible(_snapshot.WindowHandle)
                    ? _snapshot.WindowHandle : PptNativeWindow.Find((object)window);
            dynamic view = scope.Own((object)window.View);
            showPosition = SafeInt(() => (int)view.CurrentShowPosition) ?? 0;
            showState = SafeInt(() => (int)view.State);
            dynamic slide = SafeCom(scope, () => (object)view.Slide);
            if (slide is not null)
            {
                int id = (int)slide.SlideID;
                current = _snapshot.Presentation?.Identity == identity && _snapshot.Slide?.SlideId == id
                    ? _snapshot.Slide
                    : BuildSlideInfo(scope, slides, slide, id, SafeInt(() => (int)slide.SlideIndex) ?? 0, _slideIndexById);
            }
            else if (showState != 5 && _snapshot.IsSlideShowRunning && _snapshot.WindowHandle == hwnd
                && _snapshot.Presentation?.Identity == identity)
                current = _snapshot.Slide;
        }
        else
        {
            dynamic window = SafeCom(scope, () => (object)app.ActiveWindow);
            if (window is not null)
            {
                dynamic view = SafeCom(scope, () => (object)window.View);
                dynamic slide = view is null ? null : GetEditingSlide(scope, window, view);
                if (slide is not null)
                    current = BuildSlideInfo(scope, slides, slide, SafeInt(() => (int)slide.SlideID) ?? 0,
                        SafeInt(() => (int)slide.SlideIndex) ?? 0, _slideIndexById);
                hwnd = SafeNint(() => (nint)(long)window.HWND) ?? 0;
            }
        }
        return new PptSnapshot
        {
            State = inShow ? PptConnectionState.SlideShow : PptConnectionState.Editing,
            Presentation = info, Slide = current, WindowHandle = hwnd, ShowPosition = showPosition, ShowState = showState,
            IsWindowedShow = windowedShow,
        };
    }

    private static PptSlideInfo BuildSlideInfo(ComScope scope, dynamic slides, dynamic slide, int slideId, int indexHint,
        Dictionary<int, int> indexCache)
    {
        int index = indexHint;
        if (index < 1 && !indexCache.TryGetValue(slideId, out index))
        {
            for (int i = 1; i <= (int)slides.Count; i++)
            {
                dynamic candidate = scope.Own((object)slides[i]);
                int candidateId = (int)candidate.SlideID;
                if (candidateId == slideId) { index = i; break; }
            }
        }
        if (index > 0 && slideId > 0) indexCache[slideId] = index;
        dynamic transition = SafeCom(scope, () => (object)slide.SlideShowTransition);
        return new PptSlideInfo(slideId, index, SafeString(() => (string)slide.Name),
            transition is null ? null : SafeTriState(() => (int)transition.Hidden),
            transition is null ? null : SafeTriState(() => (int)transition.AdvanceOnTime),
            transition is null ? null : SafeDouble(() => (double)transition.AdvanceTime));
    }

    private static object GetShowWindow(ComScope scope, dynamic app)
    {
        // A rejected COM call during a transition must retain the last snapshot. Treating
        // that failure as an ended show clears and reloads otherwise unchanged ink.
        dynamic windows = scope.Own((object)app.SlideShowWindows);
        return (int)windows.Count > 0 ? scope.Own((object)windows[1]) : null;
    }
    private static object GetShowPresentation(ComScope scope, dynamic app)
    {
        object window = GetShowWindow(scope, app);
        return window is null ? null : GetPresentation(scope, (dynamic)window);
    }
    private static object GetPresentation(ComScope scope, dynamic window)
    {
        try { return scope.Own((object)window.Presentation); } catch { return null; }
    }
    private static object GetActivePresentation(ComScope scope, dynamic app)
    {
        try { return scope.Own((object)app.ActivePresentation); } catch { return null; }
    }
    private static int GetEditingSlideIndex(ComScope scope, dynamic app)
    {
        try
        {
            dynamic window = scope.Own((object)app.ActiveWindow);
            dynamic view = scope.Own((object)window.View);
            dynamic slide = GetEditingSlide(scope, window, view);
            return slide is null ? 0 : (int)slide.SlideIndex;
        }
        catch { return 0; }
    }
    private static object GetEditingSlide(ComScope scope, dynamic window, dynamic view)
    {
        object slide = SafeCom(scope, () => (object)view.Slide);
        if (slide is not null) return slide;
        try
        {
            dynamic selection = scope.Own((object)window.Selection);
            dynamic range = scope.Own((object)selection.SlideRange);
            if ((int)range.Count < 1) return null;
            return scope.Own((object)range[1]);
        }
        catch { return null; }
    }
    private static bool TryRunCurrentSlideCommand(ComScope scope, dynamic app)
    {
        try { dynamic bars = scope.Own((object)app.CommandBars); bars.ExecuteMso("SlideShowFromCurrent"); return true; }
        catch { return false; }
    }

    private bool InvokeShow(string operation, Action<dynamic> action, bool activateWindow = false)
    {
        VerifyThread();
        using var scope = new ComScope();
        try
        {
            if (_app is null) return false;
            dynamic app = _app;
            dynamic window = GetShowWindow(scope, app);
            if (window is null) return false;
            if (activateWindow) { try { window.Activate(); } catch { } }
            dynamic view = scope.Own((object)window.View);
            action(view);
            LastFailure = null;
            return true;
        }
        catch (Exception ex) { Fail(operation, ex.Message, Marshal.GetHRForException(ex)); return false; }
    }

    private PptSnapshot Publish(PptSnapshot snapshot)
    {
        if (snapshot != _snapshot)
        {
            _snapshot = snapshot;
            SnapshotChanged?.Invoke(snapshot);
        }
        else _snapshot = snapshot;
        return snapshot;
    }

    private void ConnectEvents()
    {
        try
        {
            // EApplication events use the same dispatch IDs across supported Office versions.
            foreach (int id in new[] { 2004, 2011, 2013, 2014 })
            {
                Action<object> handler = _ => RefreshRequested?.Invoke();
                ComEventsHelper.Combine(_app, ApplicationEvents, id, handler);
                _events.Add((id, handler));
            }
            Action<object, object> activated = (_, _) => RefreshRequested?.Invoke();
            ComEventsHelper.Combine(_app, ApplicationEvents, 2009, activated);
            _events.Add((2009, activated));
        }
        catch (Exception ex)
        {
            DisconnectEvents();
            Core.Diagnostics.FileLog.WriteLogToFile("PPT events unavailable, using polling: " + ex.Message);
        }
    }

    private void DisconnectEvents()
    {
        foreach (var entry in _events)
            try { ComEventsHelper.Remove(_app, ApplicationEvents, entry.Id, entry.Handler); }
            catch (Exception) { /* A disconnected Office process can no longer unadvise. */ }
        _events.Clear();
    }

    private void ReleasePresentationIdentity()
    {
        if (_presentationIdentity == 0) return;
        Marshal.Release(_presentationIdentity);
        _presentationIdentity = 0;
    }
    private void Fail(string operation, string message, int hr)
    {
        var failure = new PptFailure(operation, message, hr);
        LastFailure = failure;
        Failed?.Invoke(failure);
    }
    private void VerifyThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException("PptInteropService must only be used on the thread that created it.");
        if (_disposed) throw new ObjectDisposedException(nameof(PptInteropService));
    }
    private static object SafeCom(ComScope scope, Func<object> get) { try { return scope.Own(get()); } catch { return null; } }
    private static string SafeString(Func<string> get) { try { return get(); } catch { return null; } }
    private static int? SafeInt(Func<int> get) { try { return get(); } catch { return null; } }
    private static double? SafeDouble(Func<double> get) { try { return get(); } catch { return null; } }
    private static nint? SafeNint(Func<nint> get) { try { return get(); } catch { return null; } }
    private static bool? SafeTriState(Func<int> get) { try { return get() == -1; } catch { return null; } }
    private static bool IsDisconnectedError(int hr) =>
        hr == unchecked((int)0x80010108) || hr == unchecked((int)0x800401FD) ||
        hr == unchecked((int)0x800706BA) || hr == unchecked((int)0x80010007) ||
        hr == unchecked((int)0x80010012);

    private static void ReleasePointer(IntPtr pointer) { if (pointer != IntPtr.Zero) Marshal.Release(pointer); }
    private static void ReleaseComObject(object value)
    {
        if (value is not null && Marshal.IsComObject(value)) { try { Marshal.ReleaseComObject(value); } catch { } }
    }
    private sealed class ComScope : IDisposable
    {
        private readonly List<object> _objects = new();
        public object Own(object value) { if (value is not null && Marshal.IsComObject(value)) _objects.Add(value); return value; }
        public void Dispose() { for (int i = _objects.Count - 1; i >= 0; i--) ReleaseComObject(_objects[i]); _objects.Clear(); }
    }
}
