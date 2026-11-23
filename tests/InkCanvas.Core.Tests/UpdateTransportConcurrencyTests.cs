using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using InkCanvas.Core.Update;
using InkCanvas.Features.Update;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class UpdateTransportConcurrencyTests
{
    private const string AssetUrl = "https://github.com/QiXinia/Ink-Canvas-HEHS/releases/download/v3.9.0/package.zip";
    private static readonly string[] Mirrors = { "https://first.example/", "https://second.example/", "https://third.example/", "https://fourth.example/", "https://fifth.example/" };
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData("metadata")]
    [InlineData("application")]
    [InlineData("plugin")]
    public async Task FirstBatchRacesThreeSourcesAndUsesFastestValidResponse(string operation)
    {
        using var directory = new TestDirectory();
        var started = Signal();
        var release = Signal();
        var canceled = Signal();
        var requests = new ConcurrentQueue<string>();
        int canceledCount = 0;
        using var transport = new UpdateTransport(new Handler(async (request, token) =>
        {
            string host = request.RequestUri!.Host;
            requests.Enqueue(host);
            if (requests.Count == 3) started.TrySetResult();
            if (host == "second.example")
            {
                await release.Task.WaitAsync(token);
                return Response("package");
            }
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { if (Interlocked.Increment(ref canceledCount) == 2) canceled.TrySetResult(); }
            return Response("unused");
        }), Mirrors);
        var progress = new List<UpdateProgress>();
        transport.ProgressChanged += progress.Add;
        using var service = new UpdateService(transport: transport, cache: new UpdateCache(directory.Path));
        string path = System.IO.Path.Combine(directory.Path, "package.zip");
        Task<string> result = operation switch
        {
            "metadata" => transport.GetTextAsync("https://api.github.com/repos/QiXinia/Ink-Canvas-HEHS/releases", text => text == "package", CancellationToken.None),
            "plugin" => service.DownloadAssetAsync(new RepositoryAsset("plugin.zip", AssetUrl, 7, Hash("package"), null)),
            _ => DownloadAsync(),
        };
        await started.Task.WaitAsync(Deadline);
        Assert.False(result.IsCompleted);
        Assert.Equal(3, requests.Count);
        release.SetResult();
        string output = await result.WaitAsync(Deadline);
        await canceled.Task.WaitAsync(Deadline);
        Assert.Equal("second.example", transport.LastSource);
        Assert.Null(transport.LastError);
        Assert.Equal(3, requests.Count);
        Assert.Equal("package", operation == "metadata" ? output : await File.ReadAllTextAsync(output));
        Assert.All(progress, item => Assert.Equal("second.example", item.Source));

        async Task<string> DownloadAsync()
        {
            await transport.DownloadAsync(AssetUrl, path, 7, Hash("package"), null, CancellationToken.None);
            return path;
        }
    }

    [Fact]
    public async Task NextBatchStartsOnlyAfterEverySourceInFirstBatchFails()
    {
        var firstStarted = Signal();
        var failFirst = Signal();
        var requests = new ConcurrentQueue<string>();
        int firstCompleted = 0;
        using var transport = new UpdateTransport(new Handler(async (request, token) =>
        {
            string host = request.RequestUri!.Host;
            requests.Enqueue(host);
            if (host is "api.github.com" or "first.example" or "second.example")
            {
                if (requests.Count == 3) firstStarted.TrySetResult();
                await failFirst.Task.WaitAsync(token);
                Interlocked.Increment(ref firstCompleted);
                return new(HttpStatusCode.ServiceUnavailable);
            }
            Assert.Equal(3, Volatile.Read(ref firstCompleted));
            return Response("[]");
        }), Mirrors);
        var result = transport.GetTextAsync("https://api.github.com/repos/a/b/releases", text => text == "[]", CancellationToken.None);
        await firstStarted.Task.WaitAsync(Deadline);
        Assert.Equal(3, requests.Count);
        failFirst.SetResult();
        Assert.Equal("[]", await result.WaitAsync(Deadline));
        Assert.Equal(6, requests.Count);
        Assert.Equal("third.example", transport.LastSource);
    }

    [Fact]
    public async Task InvalidMetadataAndStalledBodyCannotBlockAnotherValidSource()
    {
        var stalled = new StalledStream();
        using var transport = new UpdateTransport(new Handler((request, _) => Task.FromResult(request.RequestUri!.Host switch
        {
            "api.github.com" => Response("<html>proxy error</html>"),
            "first.example" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stalled) },
            _ => Response("[]"),
        })), Mirrors);
        Assert.Equal("[]", await transport.GetTextAsync("https://api.github.com/repos/a/b/releases", text => text == "[]", CancellationToken.None).WaitAsync(Deadline));
        await stalled.Disposed.Task.WaitAsync(Deadline);
        Assert.Equal("second.example", transport.LastSource);
    }

    [Fact]
    public async Task CorruptFastestPackageRetriesOtherSourcesInSameBatch()
    {
        using var directory = new TestDirectory();
        var requests = new ConcurrentQueue<string>();
        var bodies = new ConcurrentBag<TrackedStream>();
        using var transport = new UpdateTransport(new Handler((request, _) =>
        {
            string host = request.RequestUri!.Host;
            requests.Enqueue(host);
            var body = new TrackedStream(host == "github.com" ? "corrupt" : "package");
            bodies.Add(body);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
        }), Mirrors);
        string path = System.IO.Path.Combine(directory.Path, "package.zip");
        await transport.DownloadAsync(AssetUrl, path, 7, Hash("package"), null, CancellationToken.None).WaitAsync(Deadline);
        Assert.Equal("package", await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(path + ".partial"));
        Assert.Equal("first.example", transport.LastSource);
        Assert.Equal(new[] { "github.com", "first.example", "second.example", "first.example", "second.example" }, requests);
        Assert.Equal(2, bodies.Count(body => body.Reads > 0));
        Assert.All(bodies, body => Assert.True(body.IsDisposed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationStopsEveryProbeAndPreventsNextBatch(bool download)
    {
        using var directory = new TestDirectory();
        using var cancellation = new CancellationTokenSource();
        var started = Signal();
        var stopped = Signal();
        int requests = 0, cancellations = 0;
        using var transport = new UpdateTransport(new Handler(async (_, token) =>
        {
            if (Interlocked.Increment(ref requests) == 3) started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { if (Interlocked.Increment(ref cancellations) == 3) stopped.TrySetResult(); }
            return Response("unused");
        }), Mirrors);
        string path = System.IO.Path.Combine(directory.Path, "package.zip");
        Task result = download
            ? transport.DownloadAsync(AssetUrl, path, 7, Hash("package"), null, cancellation.Token)
            : transport.GetTextAsync("https://api.github.com/repos/a/b/releases", _ => true, cancellation.Token);
        await started.Task.WaitAsync(Deadline);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result.WaitAsync(Deadline));
        await stopped.Task.WaitAsync(Deadline);
        Assert.Equal(3, requests);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".partial"));
    }

    [Fact]
    public async Task LateLosingHeadersAreDisposedWithoutDelayingWinner()
    {
        using var directory = new TestDirectory();
        var late = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = new TrackedStream("package");
        using var transport = new UpdateTransport(new Handler((request, _) => request.RequestUri!.Host == "github.com"
            ? late.Task : Task.FromResult(Response("package"))), Mirrors);
        string path = System.IO.Path.Combine(directory.Path, "package.zip");
        await transport.DownloadAsync(AssetUrl, path, 7, Hash("package"), null, CancellationToken.None).WaitAsync(Deadline);
        Assert.Equal("first.example", transport.LastSource);
        late.SetResult(new(HttpStatusCode.OK) { Content = new StreamContent(body) });
        await body.Disposed.Task.WaitAsync(Deadline);
        Assert.Equal(0, body.Reads);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static HttpResponseMessage Response(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }

    private class TrackedStream(string text) : MemoryStream(Encoding.UTF8.GetBytes(text))
    {
        public int Reads { get; private set; }
        public bool IsDisposed { get; private set; }
        public TaskCompletionSource Disposed { get; } = Signal();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            return base.ReadAsync(buffer, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
            Disposed.TrySetResult();
        }
    }

    private sealed class StalledStream() : TrackedStream("")
    {
        private readonly TaskCompletionSource<int> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(_read.Task);
        protected override void Dispose(bool disposing) { _read.TrySetResult(0); base.Dispose(disposing); }
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("InkCanvas-update-race-").FullName;
        public void Dispose() => Directory.Delete(Path, true);
    }
}
