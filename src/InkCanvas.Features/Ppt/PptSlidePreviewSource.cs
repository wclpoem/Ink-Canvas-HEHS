namespace InkCanvas.Features.Ppt;

public sealed record PptSlidePreviewPage(PptSlideInfo Slide, byte[] Image);

public interface IPptSlidePreviewTransport : IDisposable
{
    Task<PptSlidePreviewPage> RenderAsync(PptPresentationInfo presentation, int index, CancellationToken token);
}

/// <summary>Loads only requested slide indices, with bounded caching and a deadline for each export.</summary>
public sealed class PptSlidePreviewSource : IDisposable
{
    private sealed record Entry(PptSlidePreviewPage Page, long Used);
    private readonly IPptSlidePreviewTransport _transport;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<(string Presentation, int SlideCount, int Index), Entry> _images = new();
    private readonly Dictionary<(string Presentation, int SlideCount, int Index), long> _failed = new();
    private readonly TimeSpan _timeout;
    private bool _disposed;
    private long _clock, _bytes;
    public int ExportCount { get; private set; }
    public int TimeoutCount { get; private set; }
    public int CachedCount => _images.Count;
    public long CachedBytes => _bytes;

    public PptSlidePreviewSource(IPptSlidePreviewTransport transport = null, TimeSpan? timeout = null)
    {
        _transport = transport ?? new PptPreviewProcess();
        _timeout = timeout ?? TimeSpan.FromSeconds(3);
    }

    public async Task<PptSlidePreviewPage> GetPageAsync(PptPresentationInfo presentation, int index, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(presentation);
        if (index < 1 || index > presentation.SlideCount) throw new ArgumentOutOfRangeException(nameof(index));
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _gate.WaitAsync(request.Token);
        try
        {
            var key = (presentation.Identity, presentation.SlideCount, index);
            if (_images.TryGetValue(key, out var cached))
            {
                _images[key] = cached with { Used = ++_clock };
                return cached.Page;
            }
            if (_failed.TryGetValue(key, out long retryAt) && Environment.TickCount64 < retryAt)
                throw new TimeoutException("此页预览已跳过，可稍后重新打开预览重试。");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(request.Token);
            deadline.CancelAfter(_timeout);
            try
            {
                var page = await _transport.RenderAsync(presentation, index, deadline.Token).WaitAsync(deadline.Token);
                request.Token.ThrowIfCancellationRequested();
                if (page?.Slide?.SlideId is not > 0 || page.Slide.Index != index || page.Image is not { Length: > 0 and <= 2 * 1024 * 1024 })
                    throw new InvalidDataException("此页预览无效。");
                ExportCount++;
                _images[key] = new(page, ++_clock);
                _bytes += page.Image.Length;
                while (_images.Count > 32 || _bytes > 16 * 1024 * 1024)
                {
                    var oldest = _images.MinBy(pair => pair.Value.Used);
                    _images.Remove(oldest.Key);
                    _bytes -= oldest.Value.Page.Image.Length;
                }
                return page;
            }
            catch (OperationCanceledException) when (!request.IsCancellationRequested)
            {
                TimeoutCount++;
                RememberFailure(key);
                throw new TimeoutException("此页预览生成超时，已跳过。");
            }
            catch (Exception) when (request.IsCancellationRequested)
            {
                request.Token.ThrowIfCancellationRequested();
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RememberFailure(key);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    private void RememberFailure((string Presentation, int SlideCount, int Index) key)
    {
        if (_failed.Count >= 64) _failed.Remove(_failed.Keys.First());
        _failed[key] = Environment.TickCount64 + 30000;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _transport.Dispose();
        _images.Clear(); _failed.Clear(); _bytes = 0;
    }
}
