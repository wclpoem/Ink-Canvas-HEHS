using System.Net;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace InkCanvas.Features.Update;

/// <summary>Races GitHub and HTTPS mirrors in batches of three, with bounded attempts and verified downloads.</summary>
public sealed class UpdateTransport : IDisposable
{
    private const int SourceBatchSize = 3;
    private readonly HttpClient _http;
    private readonly string[] _mirrors;
    public TimeSpan HeaderTimeout { get; set; } = TimeSpan.FromSeconds(8);
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(20);
    public string LastSource { get; private set; }
    public string LastError { get; private set; }
    public event Action<string> StatusChanged;
    public event Action<UpdateProgress> ProgressChanged;

    public UpdateTransport(HttpMessageHandler handler = null, IEnumerable<string> mirrors = null)
    {
        _http = new(handler ?? new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5), AutomaticDecompression = DecompressionMethods.All });
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("InkCanvas-Updater/3.5");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");
        _mirrors = (mirrors ?? new[]
        {
            "https://ghfile.geekertao.top/",
            "https://github.dpik.top/",
            "https://gh-proxy.com/",
            "https://fastgit.cc/",
            "https://gh.acmsz.top/",
        }).ToArray();
    }

    public async Task<string> GetTextAsync(string url, Func<string, bool> validate, CancellationToken token)
    {
        LastSource = LastError = null;
        foreach (var batch in Candidates(url).Chunk(SourceBatchSize))
        {
            var result = await FirstAvailableAsync(batch.ToList(), async (candidate, cancellation) =>
            {
                using var response = await OpenAsync(candidate.Url, cancellation).ConfigureAwait(false);
                await using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
                using var memory = new MemoryStream();
                await CopyAsync(input, memory, 4 * 1024 * 1024, null, cancellation).ConfigureAwait(false);
                string text = Encoding.UTF8.GetString(memory.ToArray());
                if (!validate(text)) throw new InvalidDataException("更新源返回了无效内容。");
                return text;
            }, token).ConfigureAwait(false);
            if (result is null) continue;
            LastSource = result.Candidate.Source; LastError = null;
            return result.Value;
        }
        throw new HttpRequestException("所有更新源均无法连接。" + LastError);
    }

    public async Task DownloadAsync(string url, string path, long size, string sha256, IProgress<double> progress, CancellationToken token)
    {
        LastSource = LastError = null;
        foreach (var batch in Candidates(url).Chunk(SourceBatchSize))
        {
            var remaining = batch.ToList();
            while (remaining.Count > 0)
            {
                var result = await FirstAvailableAsync(remaining, async (candidate, cancellation) =>
                {
                    var response = await OpenAsync(candidate.Url, cancellation).ConfigureAwait(false);
                    if (response.Content.Headers.ContentLength is long contentLength && size > 0 && contentLength != size)
                    {
                        response.Dispose();
                        throw new InvalidDataException("安装包长度与发行信息不一致。");
                    }
                    return response;
                }, token).ConfigureAwait(false);
                if (result is null) break;
                string partial = path + ".partial";
                using var response = result.Value;
                try
                {
                    StatusChanged?.Invoke("正在下载 · " + result.Candidate.Source);
                    ProgressChanged?.Invoke(new(UpdateStage.Downloading, Source: result.Candidate.Source));
                    long expected = size > 0 ? size : response.Content.Headers.ContentLength ?? 0;
                    await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                    await using (var output = new FileStream(partial, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, true))
                    {
                        var watch = Stopwatch.StartNew();
                        long lastReport = -1000;
                        long count = await CopyAsync(input, output, expected > 0 ? expected : 1024L * 1024 * 1024,
                            read =>
                            {
                                if (watch.ElapsedMilliseconds - lastReport < 100 && read != expected) return;
                                lastReport = watch.ElapsedMilliseconds;
                                double? fraction = expected > 0 ? (double)read / expected : null;
                                if (fraction.HasValue) progress?.Report(fraction.Value);
                                ProgressChanged?.Invoke(new(UpdateStage.Downloading, fraction, read, expected,
                                    read / Math.Max(.001, watch.Elapsed.TotalSeconds), result.Candidate.Source));
                            }, token).ConfigureAwait(false);
                        if (expected > 0 && count != expected) throw new InvalidDataException("安装包下载不完整。");
                        ProgressChanged?.Invoke(new(UpdateStage.Verifying, DownloadedBytes: count, TotalBytes: expected, Source: result.Candidate.Source));
                        output.Position = 0;
                        string actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(output, token).ConfigureAwait(false));
                        if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装包 SHA256 校验失败。");
                    }
                    File.Move(partial, path, true);
                    LastSource = result.Candidate.Source; LastError = null;
                    return;
                }
                catch (Exception ex) when (Retryable(ex, token)) { Failed(result.Candidate.Source, ex); }
                finally { if (File.Exists(partial)) File.Delete(partial); }
            }
        }
        throw new HttpRequestException("安装包下载失败。" + LastError);
    }

    private sealed record SourceCandidate(string Url, string Source);
    private sealed record SourceResult<T>(SourceCandidate Candidate, T Value);

    private async Task<SourceResult<T>> FirstAvailableAsync<T>(List<SourceCandidate> candidates,
        Func<SourceCandidate, CancellationToken, Task<T>> probe, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        StatusChanged?.Invoke("正在并发检测 · " + string.Join(" / ", candidates.Select(candidate => candidate.Source)));
        var pending = new List<(SourceCandidate Candidate, CancellationTokenSource Lifetime, Task<T> Task)>();
        try
        {
            foreach (var candidate in candidates)
            {
                var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
                pending.Add((candidate, lifetime, probe(candidate, lifetime.Token)));
            }
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending.Select(attempt => attempt.Task)).WaitAsync(token).ConfigureAwait(false);
                int index = pending.FindIndex(attempt => attempt.Task == completed);
                var attempt = pending[index];
                pending.RemoveAt(index);
                candidates.Remove(attempt.Candidate);
                try
                {
                    T value = await completed.ConfigureAwait(false);
                    if (token.IsCancellationRequested)
                    {
                        if (value is IDisposable resource) resource.Dispose();
                        token.ThrowIfCancellationRequested();
                    }
                    return new(attempt.Candidate, value);
                }
                catch (Exception ex) when (Retryable(ex, token)) { Failed(attempt.Candidate.Source, ex); }
                finally { attempt.Lifetime.Dispose(); }
            }
            return null;
        }
        finally
        {
            foreach (var attempt in pending)
            {
                attempt.Lifetime.Cancel();
                _ = DiscardAsync(attempt.Task, attempt.Lifetime);
            }
        }
    }

    private static async Task DiscardAsync<T>(Task<T> task, CancellationTokenSource lifetime)
    {
        try { if (await task.ConfigureAwait(false) is IDisposable resource) resource.Dispose(); }
        catch (Exception) { /* Losing probes may fail or finish after cancellation. Observe and release them. */ }
        finally { lifetime.Dispose(); }
    }

    private async Task<HttpResponseMessage> OpenAsync(string url, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(HeaderTimeout);
        HttpResponseMessage response;
        try { response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException exception) when (!token.IsCancellationRequested)
        { throw new TimeoutException($"连接超时（{HeaderTimeout.TotalSeconds:0.##} 秒）。", exception); }
        if (!response.IsSuccessStatusCode) { var code = response.StatusCode; response.Dispose(); throw new HttpRequestException("HTTP " + (int)code); }
        return response;
    }

    private async Task<long> CopyAsync(Stream source, Stream destination, long limit, Action<long> progress, CancellationToken token)
    {
        var buffer = new byte[81920]; long total = 0;
        long checkpoint = 0;
        long checkpointTime = Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ReadTimeout);
        while (true)
        {
            int read;
            try
            {
                var remaining = ReadTimeout - Stopwatch.GetElapsedTime(checkpointTime);
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("下载进度超时。");
                read = await source.ReadAsync(buffer, timeout.Token).AsTask().WaitAsync(remaining, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!token.IsCancellationRequested)
            { throw new TimeoutException($"读取超时（{ReadTimeout.TotalSeconds:0.##} 秒未取得足够进度）。", exception); }
            catch (TimeoutException exception)
            {
                timeout.Cancel();
                throw new TimeoutException($"读取超时（{ReadTimeout.TotalSeconds:0.##} 秒未取得足够进度）。", exception);
            }
            if (read == 0) return total;
            total += read;
            if (total > limit) throw new InvalidDataException("更新内容超过声明大小。");
            await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            progress?.Invoke(total);
            // A trickling response must not postpone failover indefinitely.
            if (total - checkpoint >= 64 * 1024)
            { checkpoint = total; checkpointTime = Stopwatch.GetTimestamp(); timeout.CancelAfter(ReadTimeout); }
        }
    }

    private IEnumerable<SourceCandidate> Candidates(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.Host is not ("github.com" or "api.github.com") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("更新地址必须来自 GitHub HTTPS。");
        yield return new(url, "GitHub");
        foreach (string mirror in _mirrors)
        {
            var origin = new Uri(mirror);
            if (origin.Scheme != "https") throw new InvalidDataException("镜像源必须使用 HTTPS。");
            yield return new(mirror.TrimEnd('/') + "/" + url, origin.Host);
        }
    }

    private static bool Retryable(Exception exception, CancellationToken token)
        => !token.IsCancellationRequested && exception is HttpRequestException or IOException or InvalidDataException or OperationCanceledException or TimeoutException;
    private void Failed(string source, Exception exception)
    {
        LastError = source + " · " + exception.Message;
        Core.Diagnostics.FileLog.WriteLogToFile("Update: " + LastError);
        StatusChanged?.Invoke(source + " 连接或校验失败，正在切换更新源");
    }
    public void Dispose() => _http.Dispose();
}
