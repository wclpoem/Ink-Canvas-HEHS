// @parity harmony/platform/src/main/ets/assistant/AiConfig.ets
using System.Globalization;
using InkCanvas.Core.AiAssistant;

namespace InkCanvas.Platform.Ai;

/// <summary>
/// AI 助教运行时配置。端点为空时走本地 mock 流，保证无网络也能自测；
/// 真实服务端点通过 <c>AI_ENDPOINT</c> 或启动参数 <c>--ai-endpoint=</c> 注入，无需改代码。
/// 优先级：命令行覆盖 &gt; 环境变量 &gt; 默认值。
/// </summary>
public sealed class AiConfig
{
    public const string DefaultModel = "inkcanvas-assistant";
    public const int DefaultTimeoutMs = 15000;
    public const int DefaultImageTimeoutMs = 20000;
    public const int MinTimeoutMs = 1000;
    public const int MaxTimeoutMs = 120000;

    private static readonly object OverrideLock = new();
    private static string _endpointOverride;
    private static string _modelOverride;
    private static string _timeoutMsOverride;
    private static string _imageTimeoutMsOverride;

    public string Endpoint { get; set; } = "";

    public string Model { get; set; } = DefaultModel;

    /// <summary>纯墨迹阶段的超时。</summary>
    public int TimeoutMs { get; set; } = DefaultTimeoutMs;

    /// <summary>带截图精修阶段的超时，默认 20 秒。</summary>
    public int ImageTimeoutMs { get; set; } = DefaultImageTimeoutMs;

    /// <summary>
    /// 写入来自启动参数的覆盖值；空或空白值忽略，非法超时同样忽略，
    /// 以便回退到环境变量或默认值。该方法只应在启动早期调用一次。
    /// </summary>
    public static void ApplyOverrides(string endpoint, string model)
        => ApplyOverrides(endpoint, model, null, null);

    /// <summary>
    /// 写入来自启动参数的覆盖值，包含可选的超时覆盖。空或空白值忽略，
    /// 非法（非整数或非正）超时忽略；合法超时按 1000–120000 ms 钳制。
    /// </summary>
    public static void ApplyOverrides(string endpoint, string model, string timeoutMs, string imageTimeoutMs)
    {
        lock (OverrideLock)
        {
            if (!string.IsNullOrWhiteSpace(endpoint)) _endpointOverride = endpoint.Trim();
            if (!string.IsNullOrWhiteSpace(model)) _modelOverride = model.Trim();
            if (IsValidTimeout(timeoutMs)) _timeoutMsOverride = timeoutMs.Trim();
            if (IsValidTimeout(imageTimeoutMs)) _imageTimeoutMsOverride = imageTimeoutMs.Trim();
        }
    }

    /// <summary>清空命令行覆盖值，主要供测试隔离使用。</summary>
    public static void ResetOverrides()
    {
        lock (OverrideLock)
        {
            _endpointOverride = null;
            _modelOverride = null;
            _timeoutMsOverride = null;
            _imageTimeoutMsOverride = null;
        }
    }

    /// <summary>
    /// 按「命令行覆盖 &gt; 环境变量 &gt; 默认」读取配置。
    /// 空模型回退默认模型；非法或非正的超时回退默认值，合法值钳制在 1000–120000 ms。
    /// </summary>
    public static AiConfig ReadFromEnvironment()
    {
        string endpointOverride;
        string modelOverride;
        string timeoutOverride;
        string imageTimeoutOverride;
        lock (OverrideLock)
        {
            endpointOverride = _endpointOverride;
            modelOverride = _modelOverride;
            timeoutOverride = _timeoutMsOverride;
            imageTimeoutOverride = _imageTimeoutMsOverride;
        }

        return new AiConfig
        {
            Endpoint = FirstNonEmpty(endpointOverride, ReadString("AI_ENDPOINT", "")),
            Model = FirstNonEmpty(modelOverride, ReadString("AI_MODEL", DefaultModel)),
            TimeoutMs = ParseTimeoutMs(FirstNonEmpty(timeoutOverride, ReadString("AI_TIMEOUT_MS", "")), DefaultTimeoutMs),
            ImageTimeoutMs = ParseTimeoutMs(
                FirstNonEmpty(imageTimeoutOverride, ReadString("AI_IMAGE_TIMEOUT_MS", "")), DefaultImageTimeoutMs),
        };
    }

    /// <summary>按阶段选择首个 token 超时。</summary>
    public int TimeoutForStage(AssistantStage stage)
    {
        int value = stage == AssistantStage.InkOnly ? TimeoutMs : ImageTimeoutMs;
        return value > 0 ? value : (stage == AssistantStage.InkOnly ? DefaultTimeoutMs : DefaultImageTimeoutMs);
    }

    /// <summary>启动摘要，供日志使用；端点为空时显示 mock，查询参数会被脱敏。</summary>
    public string Summarize()
    {
        string endpoint = string.IsNullOrWhiteSpace(Endpoint) ? "mock" : MaskQuery(Endpoint);
        return "AI 服务：" + endpoint + " · 模型 " + Model;
    }

    /// <summary>单行摘要，供设置界面与诊断日志使用；端点为空时显示 mock 流。</summary>
    public string Describe()
    {
        return Summarize()
            + " · 无图超时 " + TimeoutMs + "ms"
            + " · 带图超时 " + ImageTimeoutMs + "ms";
    }

    private static string FirstNonEmpty(string preferred, string fallback)
        => string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

    private static string MaskQuery(string endpoint)
    {
        int query = endpoint.IndexOf('?', StringComparison.Ordinal);
        return query < 0 ? endpoint : endpoint[..query] + "?…";
    }

    private static string ReadString(string name, string fallback)
    {
        string value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? fallback : value;
    }

    private static bool IsValidTimeout(string raw)
        => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0;

    private static int ParseTimeoutMs(string raw, int fallback)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) || parsed <= 0)
        {
            return fallback;
        }
        return Math.Clamp(parsed, MinTimeoutMs, MaxTimeoutMs);
    }
}