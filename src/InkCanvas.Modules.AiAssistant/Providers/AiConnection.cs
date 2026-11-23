using System.Text.Json.Serialization;

namespace InkCanvas.Modules.AiAssistant.Providers;

[JsonConverter(typeof(JsonStringEnumConverter<AiApiFormat>))]
public enum AiApiFormat { ChatCompletions, Responses, Anthropic, Google }

public sealed record AiConnection
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "自定义连接";
    public string Provider { get; init; } = "custom";
    public string BaseUrl { get; init; } = "";
    public string ModelsUrl { get; init; } = "";
    public AiApiFormat Format { get; init; }
    public string Model { get; init; } = "";
    public bool Search { get; init; }
    public bool SendImages { get; init; } = true;
    public bool SendFiles { get; init; } = true;
    public string Reasoning { get; init; } = "auto";
    public int MaxOutputTokens { get; init; } = 8192;
}

public sealed record AiModel(string Id, string Name);
public sealed record AiConnectionState(string ActiveId, IReadOnlyList<AiConnection> Connections);
public sealed record AiProviderPreset(string Id, string Name, string Icon, string BaseUrl, AiApiFormat Format,
    string ModelsUrl = "", string Documentation = "")
{
    public AiConnection Create() => new() { Name = Name, Provider = Id, BaseUrl = BaseUrl, Format = Format, ModelsUrl = ModelsUrl };
}

public static class AiProviderCatalog
{
    public static IReadOnlyList<AiProviderPreset> Presets { get; } = new AiProviderPreset[]
    {
        new("openai", "OpenAI · Chat", "openai", "https://api.openai.com/v1", AiApiFormat.ChatCompletions,
            Documentation: "https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create"),
        new("openai-responses", "OpenAI · Responses", "openai", "https://api.openai.com/v1", AiApiFormat.Responses,
            Documentation: "https://developers.openai.com/api/reference/resources/responses/methods/create"),
        new("anthropic", "Anthropic", "claude", "https://api.anthropic.com/v1", AiApiFormat.Anthropic,
            Documentation: "https://platform.claude.com/docs/en/api/messages"),
        new("google", "Google Gemini", "gemini", "https://generativelanguage.googleapis.com/v1beta", AiApiFormat.Google,
            Documentation: "https://ai.google.dev/api/generate-content"),
        new("deepseek", "DeepSeek", "deepseek", "https://api.deepseek.com/v1", AiApiFormat.ChatCompletions,
            Documentation: "https://api-docs.deepseek.com/guides/web_tools/"),
        new("glm", "GLM · 智谱", "zhipu", "https://open.bigmodel.cn/api/paas/v4", AiApiFormat.ChatCompletions,
            Documentation: "https://docs.bigmodel.cn/"),
        new("tokenhub", "Tencent Token Hub", "tencent", "https://tokenhub.tencentmaas.com/v1", AiApiFormat.ChatCompletions,
            Documentation: "https://cloud.tencent.com/document/product/1823/130078"),
        new("qwen", "阿里云百炼 · Qwen", "qwen", "https://dashscope.aliyuncs.com/compatible-mode/v1", AiApiFormat.ChatCompletions,
            Documentation: "https://help.aliyun.com/zh/model-studio/web-search"),
        new("volcengine", "火山引擎 · 方舟", "doubao", "https://ark.cn-beijing.volces.com/api/v3", AiApiFormat.ChatCompletions,
            Documentation: "https://www.volcengine.com/docs/82379/1298454"),
        new("stepfun", "StepFun · 阶跃星辰", "stepfun", "https://api.stepfun.com/v1", AiApiFormat.ChatCompletions,
            Documentation: "https://platform.stepfun.com/"),
        new("mimo", "MiMo · 小米", "xiaomimimo", "https://api.xiaomimimo.com/v1", AiApiFormat.ChatCompletions,
            Documentation: "https://mimo.mi.com/docs/zh-CN/quick-start/summary/first-api-call"),
        new("minimax", "MiniMax", "minimax", "https://api.minimax.cn/v1", AiApiFormat.ChatCompletions,
            Documentation: "https://platform.minimax.cn/docs/api-reference/text-openai-api"),
        new("kimi", "Kimi · 开放平台", "kimi", "https://api.moonshot.cn/v1", AiApiFormat.ChatCompletions,
            Documentation: "https://platform.moonshot.cn/docs/intro"),
        new("kimi-code", "Kimi.com · Kimi Code", "kimi", "https://api.kimi.com/coding/v1", AiApiFormat.ChatCompletions,
            Documentation: "https://www.kimi.com/code/docs/en/"),
        new("opencode", "OpenCode", "opencode", "https://opencode.ai/inference/openai/v1", AiApiFormat.ChatCompletions,
            "https://opencode.ai/inference/v1/models", "https://opencode.ai/docs/console/api/inference/"),
        new("packycode", "PackyCode", "packycode", "https://codex-api.packycode.com/v1", AiApiFormat.Responses,
            Documentation: "https://codex.packycode.com/docs"),
        new("custom", "自定义 / API Gateway", "newapi", "", AiApiFormat.ChatCompletions),
    };

    public static string FormatName(AiApiFormat format) => format switch
    {
        AiApiFormat.ChatCompletions => "OpenAI Chat Completions",
        AiApiFormat.Responses => "OpenAI Responses",
        AiApiFormat.Anthropic => "Anthropic Messages",
        _ => "Google Gemini",
    };

    public static bool SupportsSearch(AiConnection connection) => connection.Provider switch
    {
        "deepseek" => connection.Format != AiApiFormat.Google,
        "qwen" => connection.Format is AiApiFormat.ChatCompletions or AiApiFormat.Responses,
        "openai" or "openai-responses" => connection.Format == AiApiFormat.Responses,
        "google" => connection.Format == AiApiFormat.Google,
        _ => false,
    };
}
