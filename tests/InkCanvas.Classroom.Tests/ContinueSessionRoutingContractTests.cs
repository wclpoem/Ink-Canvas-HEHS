using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 「变式预览走 continueSession、板书/写回仍走 writeBack」的源码静态契约：
/// ShellController 依赖 WinUI/窗口对象，无法在纯逻辑测试中实例化，
/// 因此直接在方法体内断言校验参数，防止后续重构把预览重新降级为 writeBack。
/// </summary>
public sealed class ContinueSessionRoutingContractTests
{
    private const string OnAiDoneSignature = "private void OnAiDone()";
    private const string FlushSignature = "private void FlushPendingExamplePrompt()";
    private const string RecordExplanationSignature = "private void RecordAiExplanation()";
    private const string ConfirmSignature = "private async Task ConfirmBoardWriteAsync(";
    private const string SelectGroupSignature = "private static void SelectAiBoardGroup(";
    private const string PreviewSignature = "private async Task PreviewVariantAsync(AssistantSession session)";
    private const string StartPracticeSignature = "private async Task StartPracticeAsync(";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "InkCanvas.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ShellControllerSource()
    {
        string path = Path.Combine(RepoRoot(), "src", "InkCanvas.App", "Services", "ShellController.cs");
        Assert.True(File.Exists(path), "缺少文件：src/InkCanvas.App/Services/ShellController.cs");
        return File.ReadAllText(path);
    }

    private static string MethodBody(string source, string signature, string nextSignature, string methodName)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"未找到 {methodName}（签名：{signature}）");
        int end = source.IndexOf(nextSignature, start + signature.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"未找到 {methodName} 的结束边界（下一个方法：{nextSignature}）");
        return source[start..end];
    }

    [Fact]
    public void OnAiDone_RoutesVariantPendingThroughContinueSession()
    {
        string body = MethodBody(
            ShellControllerSource(), OnAiDoneSignature, FlushSignature, "OnAiDone");

        Assert.Contains("_variantPending", body);
        Assert.Contains("AiTargetStillCurrent(continueSession: true)", body);
        Assert.DoesNotContain("AiTargetStillCurrent(writeBack: true)", body);

        const string expectedPreviewCall = "_ = PreviewVariantSafelyAsync(_aiSession)";
        Assert.Contains(expectedPreviewCall, body);
        Assert.DoesNotContain("_ = PreviewVariantAsync(_aiSession)", body);

        int previewCall = body.IndexOf(expectedPreviewCall, StringComparison.Ordinal);
        Assert.True(previewCall >= 0, "OnAiDone 应在 continueSession 守卫通过后调用 PreviewVariantSafelyAsync(_aiSession)。");
        int previewGuard = body.LastIndexOf(
            "if (AiTargetStillCurrent(continueSession: true))", previewCall, StringComparison.Ordinal);
        Assert.True(previewGuard >= 0, "PreviewVariantSafelyAsync(_aiSession) 调用前必须存在 continueSession 守卫。");
    }

    [Fact]
    public void FlushPendingExamplePrompt_UsesContinueSessionAndClearsPendingPrompt()
    {
        string body = MethodBody(
            ShellControllerSource(), FlushSignature, RecordExplanationSignature, "FlushPendingExamplePrompt");

        Assert.Contains("AiTargetStillCurrent(continueSession: true)", body);
        Assert.Contains("_aiPendingExamplePrompt = \"\"", body);
        Assert.DoesNotContain("writeBack: true", body);
    }

    [Fact]
    public void ConfirmBoardWriteAsync_StillRequiresWriteBack()
    {
        string body = MethodBody(
            ShellControllerSource(), ConfirmSignature, SelectGroupSignature, "ConfirmBoardWriteAsync");

        Assert.Contains("AiTargetStillCurrent(writeBack: true)", body);
        Assert.DoesNotContain("continueSession: true", body);
    }

    [Fact]
    public void PreviewVariantAsync_LogsSkipFailureAndPopupWithAiPrefix()
    {
        string body = MethodBody(
            ShellControllerSource(), PreviewSignature, StartPracticeSignature, "PreviewVariantAsync");

        Assert.Contains("[AI] 变式预览", body);
        Assert.Contains("变式预览跳过", body);
        Assert.Contains("变式预览失败", body);
        Assert.Contains("变式预览弹出", body);
    }
}