using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 例题库接入 UI 的静态契约：UI 层依赖 WinUI，无法在纯逻辑测试中实例化窗口，
/// 因此直接对源码断言关键调用存在，防止入口、检索、删除与功能开关被后续改动丢失。
/// </summary>
public sealed class ExampleLibraryUiContractTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "InkCanvas.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ReadSource(string relativePath)
    {
        string path = Path.Combine(
            RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), "缺少文件：" + relativePath);
        return File.ReadAllText(path);
    }

    [Fact]
    public void ExampleLibraryWindow_UsesStoreFeatureGateAndTheme()
    {
        string source = ReadSource("src/InkCanvas.App/Views/ExampleLibraryWindow.cs");

        Assert.Contains("ExampleLibraryStore.Search", source);
        Assert.Contains("ExampleLibraryStore.Delete", source);
        Assert.Contains("EnableExampleLibrary", source);
        Assert.Contains("ThemeBrushes.Resolve", source);
        Assert.Contains("WindowChrome.ApplyTheme", source);
        Assert.Contains("例题库", source);
    }

    [Fact]
    public void AssistantWindow_Favorite_WritesAiAssociation()
    {
        string source = ReadSource("src/InkCanvas.App/Views/AssistantWindow.cs");

        Assert.Contains("AiSessionId = _sourceSessionId", source);
        Assert.Contains("RequestId = _sourceRequestId", source);
        Assert.Contains("BoardId = _sourceBoardId", source);
        Assert.Contains("BoardPageId = _sourceBoardPageId", source);
        Assert.Contains("ObjectIds = new List<string>(_sourceObjectIds)", source);
        Assert.Contains("StepsJson = ExampleEntryCodec.EncodeSteps", source);
        Assert.Contains("AnnotationsJson = ExampleEntryCodec.EncodeAnnotations", source);
        Assert.Contains("SetFavoriteObjects", source);
    }

    [Fact]
    public void ExampleLibraryWindow_ExposesStage3AActions()
    {
        string source = ReadSource("src/InkCanvas.App/Views/ExampleLibraryWindow.cs");

        Assert.Contains("IExampleLibraryHost", source);
        Assert.Contains("再讲一次", source);
        Assert.Contains("贴回白板", source);
        Assert.Contains("编辑订正", source);
        Assert.Contains("ReExplainExample", source);
        Assert.Contains("PasteExampleToBoardAsync", source);
        Assert.Contains("TeacherEdited = true", source);
        Assert.Contains("UpdatedAt = DateTime.Now", source);
    }

    [Fact]
    public void ShellController_ImplementsExampleHostAndSummaryPage()
    {
        string source = ReadSource("src/InkCanvas.App/Services/ShellController.cs");

        Assert.Contains("IExampleLibraryHost", source);
        Assert.Contains("ExampleReExplainPrompt.Build", source);
        Assert.Contains("ExampleBoardPlanFactory", source);
        Assert.Contains("SummaryBoardPolicy.IsSummary", source);
        Assert.Contains("SummaryPageName", source);
        Assert.Contains("SetFavoriteObjects", source);
        Assert.Contains("HandoutComposer.Summarize", source);
    }

    [Fact]
    public void ToolkitWindow_ExposesExampleLibraryEntry()
    {
        string source = ReadSource("src/InkCanvas.App/Views/ToolkitWindow.cs");

        Assert.Contains("例题库", source);
        Assert.Contains("_openExampleLibrary", source);
    }

    [Fact]
    public void ShellController_ExposesSingleInstanceOpenMethod()
    {
        string source = ReadSource("src/InkCanvas.App/Services/ShellController.cs");

        Assert.Contains("public void OpenExampleLibrary()", source);
        Assert.Contains("new ExampleLibraryWindow(", source);
        Assert.Contains("_exampleLibrary", source);
    }
}