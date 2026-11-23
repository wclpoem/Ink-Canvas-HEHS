// @parity harmony/platform/src/main/ets/assistant/AiClient.ets
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using InkCanvas.Core.AiAssistant;
using InkCanvas.Modules.AiAssistant.Providers;

namespace InkCanvas.Platform.Ai;

/// <summary>
/// AI 助教流式客户端。端点为空时回放本地 mock 剧本，配置端点后按 6.1 请求体发 POST，
/// 逐行喂 <see cref="SseStreamParser"/>；首 token 超时与 Abort 均不再回调。
/// </summary>
public sealed class AiClient : IDisposable
{
    internal static readonly HttpClient SharedHttpClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static readonly JsonSerializerOptions RequestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly AiConfig _config;
    private readonly AiConnection _connection;
    private readonly string _key;
    private bool _disposed;

    public AiClient(AiConfig config)
    {
        _config = config ?? new AiConfig();
    }

    public AiConfig Config => _config;

    public AiClient(AiConnection connection, string key)
    {
        _connection = connection;
        _key = key;
        _config = new AiConfig { Endpoint = connection.BaseUrl, Model = connection.Model };
    }

    /// <summary>
    /// 启动一次流式请求；<paramref name="onEvent"/> 逐事件回调，结束时恰好触发一次
    /// <paramref name="onDone"/> 或 <paramref name="onError"/>。返回的句柄用于中断，
    /// Abort 之后不会再有任何回调。
    /// </summary>
    public AiStreamHandle Start(
        AssistantRequest request,
        Action<AssistantEvent> onEvent,
        Action<string> onError,
        Action onDone)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AiClient));

        request ??= new AssistantRequest();
        if (_connection is not null)
            return new AiAssistantProviderStream(_connection, _key, request, onEvent, onError, onDone).Handle;
        if (string.IsNullOrEmpty(_config.Endpoint))
        {
            var mock = new MockStream(
                request.Mode, request.Teaching, request.Purpose, request.Difficulty,
                onEvent, onError, onDone);
            return new AiStreamHandle(mock.Abort);
        }

        var http = new HttpStream(_config, request, onEvent, onError, onDone);
        return new AiStreamHandle(http.Abort);
    }

    /// <summary>
    /// 两阶段请求：先发 <see cref="AssistantStage.InkOnly"/> 请求，收到首个 Token 后自动补发
    /// <see cref="AssistantStage.WithImage"/> 请求并合并回调；完成回调恰好一次，Abort 会同时中止两段。
    /// 端点为空走 mock 流时两段都会重放同一剧本，调用方需按语义去重。
    /// 第二段启动后不再转发第一段遗留的事件，避免调用方清空草稿后被旧阶段回填。
    /// <see cref="AiStreamHandle.SecondStageStarted"/> 在启动第二段之前恰好触发一次。
    /// </summary>
    public AiStreamHandle StartTwoStage(
        AssistantRequest request,
        Action<AssistantEvent> onEvent,
        Action<string> onError,
        Action onDone)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AiClient));

        request ??= new AssistantRequest();
        var gate = new object();
        AiStreamHandle first = null;
        AiStreamHandle second = null;
        AiStreamHandle handle = null;
        bool aborted = false;
        bool completed = false;
        bool secondStarted = false;

        void FinishError(string message)
        {
            lock (gate)
            {
                if (aborted || completed) return;
                completed = true;
            }

            onError?.Invoke(message ?? "");
        }

        void Finish()
        {
            lock (gate)
            {
                if (aborted || completed) return;
                completed = true;
            }

            onDone?.Invoke();
        }

        void StartSecondStage()
        {
            lock (gate)
            {
                if (aborted || completed || secondStarted) return;
                secondStarted = true;
                handle?.RaiseSecondStageStarted();
                second = Start(
                    WithStage(request, AssistantStage.WithImage),
                    onEvent,
                    FinishError,
                    Finish);
            }
        }

        first = Start(
            WithStage(request, AssistantStage.InkOnly),
            ev =>
            {
                lock (gate)
                {
                    if (aborted || secondStarted) return;
                }

                onEvent?.Invoke(ev);
                if (ev != null && ev.Kind == AssistantEventKind.Token) StartSecondStage();
            },
            FinishError,
            () =>
            {
                bool waiting = false;
                lock (gate)
                {
                    if (!aborted && !completed && !secondStarted) waiting = true;
                }

                if (waiting) Finish();
            });

        handle = new AiStreamHandle(() =>
        {
            AiStreamHandle a;
            AiStreamHandle b;
            lock (gate)
            {
                aborted = true;
                a = first;
                b = second;
            }

            a?.Abort();
            b?.Abort();
        });

        return handle;
    }

    private static AssistantRequest WithStage(AssistantRequest request, AssistantStage stage)
    {
        return new AssistantRequest
        {
            ImageBase64 = request.ImageBase64 ?? "",
            InkStrokes = request.InkStrokes,
            Viewport = request.Viewport,
            Mode = request.Mode,
            SessionId = request.SessionId ?? "",
            Question = request.Question ?? "",
            Stage = stage,
            Region = request.Region,
            Images = request.Images,
            Teaching = request.Teaching,
            Purpose = request.Purpose ?? "",
            Instruction = request.Instruction ?? "",
            TargetPageIndex = request.TargetPageIndex,
            Difficulty = request.Difficulty ?? "",
            SlideContext = request.SlideContext,
            Messages = request.Messages,
        };
    }

    /// <summary>
    /// 释放客户端。进行中的流不会自动中断，调用方应先 Abort 对应的 <see cref="AiStreamHandle"/>。
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
    }

    /// <summary>构造请求体；新增字段一律追加，保证既有 6 个字段的顺序与含义不变。</summary>
    public static string BuildPayload(AssistantRequest request, string model)
    {
        request ??= new AssistantRequest();

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", string.IsNullOrEmpty(model) ? AiConfig.DefaultModel : model);
            writer.WriteString("mode", ModeText(request.Mode));
            writer.WriteString("sessionId", request.SessionId ?? "");
            writer.WriteString(
                "imageBase64",
                request.Stage == AssistantStage.InkOnly ? "" : (request.ImageBase64 ?? ""));
            writer.WritePropertyName("inkStrokes");
            JsonSerializer.Serialize<IReadOnlyList<AssistantStroke>>(
                writer,
                request.InkStrokes ?? Array.Empty<AssistantStroke>(),
                RequestJson);
            writer.WritePropertyName("viewport");
            WriteViewport(writer, request.Viewport ?? new AssistantViewport());
            writer.WriteString("stage", StageText(request.Stage));
            if (!string.IsNullOrWhiteSpace(request.Question)) writer.WriteString("question", request.Question);
            if (request.Messages is { Count: > 0 })
            {
                writer.WritePropertyName("messages");
                JsonSerializer.Serialize(writer, request.Messages, RequestJson);
            }
            writer.WriteString("teaching", AssistantTeachingPolicy.Wire(request.Teaching));
            if (!string.IsNullOrEmpty(request.Purpose))
                writer.WriteString("purpose", request.Purpose);
            if (!string.IsNullOrEmpty(request.Instruction))
                writer.WriteString("instruction", request.Instruction);
            if (request.TargetPageIndex >= 0)
                writer.WriteNumber("targetPageIndex", request.TargetPageIndex);
            if (!string.IsNullOrEmpty(request.Difficulty))
                writer.WriteString("difficulty", request.Difficulty);
            if (request.SlideContext is not null)
            {
                writer.WritePropertyName("slideContext");
                WriteSlideContext(writer, request.SlideContext);
            }

            if (request.Region != null)
            {
                writer.WritePropertyName("region");
                WriteRegion(writer, request.Region);
            }

            if (request.Images is { Count: > 0 })
            {
                writer.WritePropertyName("images");
                writer.WriteStartArray();
                foreach (ProblemRegion image in request.Images)
                {
                    if (image is null) continue;
                    WriteImage(writer, image, request.Stage);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static void WriteRegion(Utf8JsonWriter writer, AssistantRegion region)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("rect");
        WriteRect(writer, region.Rect);
        writer.WriteNumber("textStart", region.TextStart);
        writer.WriteNumber("textLength", region.TextLength);
        writer.WriteEndObject();
    }

    private static void WriteImage(Utf8JsonWriter writer, ProblemRegion image, AssistantStage stage)
    {
        writer.WriteStartObject();
        writer.WriteString("id", image.Id ?? "");
        writer.WriteNumber("order", image.Order);
        writer.WriteString("text", image.Text ?? "");
        writer.WriteString(
            "imageBase64",
            stage == AssistantStage.InkOnly ? "" : (image.ImageBase64 ?? ""));
        writer.WritePropertyName("rect");
        WriteRect(writer, image.Anchor);
        writer.WriteEndObject();
    }

    private static void WriteSlideContext(Utf8JsonWriter writer, SlideContext context)
    {
        writer.WriteStartObject();
        writer.WriteNumber("version", context.Version);
        writer.WriteString("title", context.Title ?? "");
        writer.WriteNumber("slideId", context.SlideId);
        writer.WriteNumber("slideIndex", context.SlideIndex);
        writer.WriteNumber("animationStep", context.AnimationStep);
        writer.WriteString("currentText", context.CurrentText ?? "");
        writer.WriteString("previousText", context.PreviousText ?? "");
        writer.WriteString("nextText", context.NextText ?? "");
        writer.WriteString("selectionText", context.SelectionText ?? "");
        writer.WriteString("prompt", context.ToPrompt());
        writer.WriteEndObject();
    }

    private static void WriteViewport(Utf8JsonWriter writer, AssistantViewport viewport)
    {
        writer.WriteStartObject();
        writer.WriteNumber("scale", viewport.Scale);
        writer.WritePropertyName("rect");
        WriteRect(writer, viewport.Rect);
        writer.WriteEndObject();
    }

    private static void WriteRect(Utf8JsonWriter writer, AssistantRect rect)
    {
        writer.WriteStartObject();
        writer.WriteNumber("left", rect.Left);
        writer.WriteNumber("top", rect.Top);
        writer.WriteNumber("right", rect.Right);
        writer.WriteNumber("bottom", rect.Bottom);
        writer.WriteEndObject();
    }

    private static string StageText(AssistantStage stage) =>
        stage == AssistantStage.InkOnly ? "inkOnly" : "withImage";

    private static string ModeText(AssistantMode mode) => mode switch
    {
        AssistantMode.Formula => "formula",
        AssistantMode.Solve => "solve",
        AssistantMode.Shape => "shape",
        AssistantMode.Chat => "chat",
        _ => "auto",
    };
}

/// <summary>流句柄：Abort 幂等，调用后不再触发事件、错误或完成回调。</summary>
public sealed class AiStreamHandle
{
    private Action _abort;

    internal AiStreamHandle(Action abort)
    {
        _abort = abort ?? throw new ArgumentNullException(nameof(abort));
    }

    /// <summary>两阶段请求中，WithImage 阶段启动之前恰好触发一次；Abort 后不再触发。</summary>
    public event Action SecondStageStarted;

    internal void RaiseSecondStageStarted() => SecondStageStarted?.Invoke();

    public void Abort()
    {
        Interlocked.Exchange(ref _abort, null)?.Invoke();
    }
}

/// <summary>本地 mock 流：首条 60 ms，其余每条间隔 45 ms，可随时 Abort。</summary>
internal sealed class MockStream
{
    private const int FirstDelayMs = 60;
    private const int StepDelayMs = 45;

    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<AssistantEvent> _onEvent;
    private readonly Action<string> _onError;
    private readonly Action _onDone;
    private bool _aborted;

    public MockStream(
        AssistantMode mode,
        AssistantTeachingMode teaching,
        string purpose,
        string difficulty,
        Action<AssistantEvent> onEvent,
        Action<string> onError,
        Action onDone)
    {
        _onEvent = onEvent;
        _onError = onError;
        _onDone = onDone;
        _ = RunAsync(AiMockScript.Build(mode, teaching, purpose, difficulty));
    }

    public void Abort()
    {
        lock (_gate)
        {
            _aborted = true;
        }

        Cancel();
    }

    private async Task RunAsync(IReadOnlyList<AssistantEvent> script)
    {
        try
        {
            await Task.Delay(FirstDelayMs, _cts.Token).ConfigureAwait(false);

            for (int i = 0; i < script.Count; i++)
            {
                lock (_gate)
                {
                    if (_aborted) return;

                    AssistantEvent item = script[i];
                    if (item.Kind == AssistantEventKind.Error)
                    {
                        _onError?.Invoke(item.Text ?? "");
                        return;
                    }

                    _onEvent?.Invoke(item);
                    if (_aborted) return;

                    if (item.Kind == AssistantEventKind.Done)
                    {
                        _onDone?.Invoke();
                        return;
                    }
                }

                if (i < script.Count - 1)
                {
                    await Task.Delay(StepDelayMs, _cts.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Abort 引发：按要求静默结束，不再回调。
        }
        finally
        {
            _cts.Dispose();
        }
    }

    private void Cancel()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

/// <summary>HTTP SSE 流：首个 Token 之前超时即中断并报错，非 2xx 直接报 HTTP 状态码。</summary>
internal sealed class HttpStream
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly int _timeoutMs;
    private readonly string _endpoint;
    private readonly string _payload;
    private readonly Action<AssistantEvent> _onEvent;
    private readonly Action<string> _onError;
    private readonly Action _onDone;
    private readonly Timer _timeoutTimer;
    private HttpRequestMessage _request;
    private int _ended;
    private bool _gotToken;

    public HttpStream(
        AiConfig config,
        AssistantRequest request,
        Action<AssistantEvent> onEvent,
        Action<string> onError,
        Action onDone)
    {
        _timeoutMs = config.TimeoutForStage(request?.Stage ?? AssistantStage.WithImage);
        _endpoint = config.Endpoint;
        _payload = AiClient.BuildPayload(request, config.Model);
        _onEvent = onEvent;
        _onError = onError;
        _onDone = onDone;
        _timeoutTimer = new Timer(_ => OnFirstTokenTimeout(), null, _timeoutMs, Timeout.Infinite);
        _ = RunAsync();
    }

    public void Abort()
    {
        lock (_gate)
        {
            _ended = 1;
        }

        StopTimer();
        Cancel();
    }

    private async Task RunAsync()
    {
        var parser = new SseStreamParser();
        try
        {
            _request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = CreateContent(_payload),
            };
            _request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            using HttpResponseMessage response = await AiClient.SharedHttpClient
                .SendAsync(_request, HttpCompletionOption.ResponseHeadersRead, _cts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Fail("HTTP " + (int)response.StatusCode);
                return;
            }

            using Stream stream = await response.Content.ReadAsStreamAsync(_cts.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (true)
            {
                string line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (line == null) break;
                if (HasEnded) return;

                Emit(parser.Push(line + "\n"));
                if (HasEnded) return;
            }

            Emit(parser.Flush());
            if (Interlocked.CompareExchange(ref _ended, 1, 0) == 0)
            {
                StopTimer();
                _onDone?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            // Abort 或首 token 超时引发；两条路径各自负责是否回调。
        }
        catch (Exception ex)
        {
            Fail(string.IsNullOrEmpty(ex.Message) ? "网络请求失败" : ex.Message);
        }
        finally
        {
            StopTimer();
            _request?.Dispose();
            _cts.Dispose();
        }
    }

    private static StringContent CreateContent(string payload)
    {
        var content = new StringContent(payload, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private bool HasEnded => Volatile.Read(ref _ended) == 1;

    private void Emit(IReadOnlyList<AssistantEvent> events)
    {
        if (events == null) return;

        lock (_gate)
        {
            if (_ended == 1) return;

            for (int i = 0; i < events.Count; i++)
            {
                AssistantEvent item = events[i];
                if (item == null) continue;

                if (item.Kind is AssistantEventKind.Token or AssistantEventKind.BoardPlan)
                {
                    _gotToken = true;
                    StopTimer();
                }

                _onEvent?.Invoke(item);
                if (_ended == 1) return;

                if (item.Kind == AssistantEventKind.Done)
                {
                    _ended = 1;
                    StopTimer();
                    _onDone?.Invoke();
                    return;
                }

                if (item.Kind == AssistantEventKind.Error)
                {
                    _ended = 1;
                    StopTimer();
                    _onError?.Invoke(item.Text ?? "");
                    return;
                }
            }
        }
    }

    private void Fail(string message)
    {
        if (Interlocked.CompareExchange(ref _ended, 1, 0) != 0) return;

        StopTimer();
        _onError?.Invoke(message);
    }

    private void OnFirstTokenTimeout()
    {
        bool fire = false;
        lock (_gate)
        {
            if (_ended == 0 && !_gotToken)
            {
                _ended = 1;
                fire = true;
            }
        }

        if (!fire) return;

        StopTimer();
        Cancel();
        _onError?.Invoke("首 token 超时（" + _timeoutMs + "ms）");
    }

    private void StopTimer()
    {
        _timeoutTimer.Dispose();
    }

    private void Cancel()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
