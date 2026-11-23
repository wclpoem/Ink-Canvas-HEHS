using InkCanvas.Core.Ui;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 对话框宿主选择：当前目标板优先，浮条/AI 浮窗只做兜底；板/文档宿主需暂时收起 AI 浮窗。
/// 同时以源码契约锁定 ShellController 不再把 ContentDialog 固定挂到浮条 XamlRoot。
/// </summary>
public sealed class DialogHostPolicyTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "InkCanvas.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static DialogHostContext Context(
        bool sideVisible = false, bool sidePreferred = false, bool mainVisible = false,
        bool documentVisible = false, bool barUsable = false, bool assistantUsable = false)
        => new(sideVisible, sidePreferred, mainVisible, documentVisible, barUsable, assistantUsable);

    [Fact]
    public void Select_PreferredSideBoard_WinsOverMainBoard()
    {
        Assert.Equal(DialogHostSurface.SideBoard,
            DialogHostPolicy.Select(Context(sideVisible: true, sidePreferred: true, mainVisible: true)));
    }

    [Fact]
    public void Select_MainBoard_WithoutSidePreference()
    {
        Assert.Equal(DialogHostSurface.MainBoard,
            DialogHostPolicy.Select(Context(sideVisible: true, sidePreferred: false, mainVisible: true)));
    }

    [Fact]
    public void Select_VisibleSideBoardOnly()
    {
        Assert.Equal(DialogHostSurface.SideBoard,
            DialogHostPolicy.Select(Context(sideVisible: true)));
    }

    [Fact]
    public void Select_FallsBackToDocumentThenBarThenAssistant()
    {
        Assert.Equal(DialogHostSurface.Document,
            DialogHostPolicy.Select(Context(documentVisible: true, barUsable: true, assistantUsable: true)));
        Assert.Equal(DialogHostSurface.FloatingBar,
            DialogHostPolicy.Select(Context(barUsable: true, assistantUsable: true)));
        Assert.Equal(DialogHostSurface.Assistant,
            DialogHostPolicy.Select(Context(assistantUsable: true)));
        Assert.Equal(DialogHostSurface.None, DialogHostPolicy.Select(Context()));
    }

    [Fact]
    public void AssistantSuspension_OnlyForBoardAndDocumentHosts()
    {
        Assert.True(DialogHostPolicy.RequiresAssistantSuspension(DialogHostSurface.MainBoard));
        Assert.True(DialogHostPolicy.RequiresAssistantSuspension(DialogHostSurface.SideBoard));
        Assert.True(DialogHostPolicy.RequiresAssistantSuspension(DialogHostSurface.Document));
        Assert.False(DialogHostPolicy.RequiresAssistantSuspension(DialogHostSurface.FloatingBar));
        Assert.False(DialogHostPolicy.RequiresAssistantSuspension(DialogHostSurface.Assistant));
        Assert.False(DialogHostPolicy.RequiresAssistantSuspension(DialogHostSurface.None));
        Assert.False(DialogHostPolicy.IsUsable(DialogHostSurface.None));
    }

    [Fact]
    public void ShellController_ContentDialog_UsesTargetWindowHost()
    {
        string path = Path.Combine(
            RepoRoot(), "src", "InkCanvas.App", "Services", "ShellController.cs");
        Assert.True(File.Exists(path));
        string source = File.ReadAllText(path);

        Assert.Contains("ResolveDialogHost", source);
        Assert.Contains("DialogHostPolicy.Select", source);
        Assert.Contains("PrepareDialogHost", source);
        Assert.Contains("SuspendAssistantForDialog", source);
        Assert.Contains("dialog.XamlRoot = host.Root", source);
        Assert.Contains("RestoreFoldedBar", source);
        Assert.Contains("_bar?.SetFolded(false)", source);
        Assert.Contains("_bar?.SetFolded(true)", source);
        Assert.DoesNotContain("dialog.XamlRoot = root.XamlRoot;\n        return await dialog.ShowAsync();", source);
        Assert.Contains("await ShowDialogAsync(dialog)", source);
    }
}