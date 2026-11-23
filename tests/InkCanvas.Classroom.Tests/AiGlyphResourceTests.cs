using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// AI 图标资源引用回归：UI 层依赖 WinUI，无法在纯逻辑测试里实例化控件，
/// 因此对源码做静态断言，防止「AI」文本与字号再次散落到各组件。
/// </summary>
public sealed class AiGlyphResourceTests
{
    private static readonly string[] CallSites =
    {
        "src/InkCanvas.Controls/Controls/TouchIconButton.cs",
        "src/InkCanvas.App/Views/ToolkitWindow.cs",
        "src/InkCanvas.Modules.AiAssistant/AssistantWindow.cs",
        "src/InkCanvas.App/Views/SlideNavigatorWindow.cs",
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "InkCanvas.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void AiGlyph_IsTheSingleResourceAndCoversIconSizes()
    {
        string path = Path.Combine(RepoRoot(), "src/InkCanvas.Controls/Controls/AiGlyph.cs");
        Assert.True(File.Exists(path), "缺少统一的 AiGlyph 资源层");

        string source = File.ReadAllText(path);
        Assert.Contains("public const string Text = \"AI\"", source);
        Assert.Contains("16d, 20d, 24d", source);
        Assert.Contains("CreateLabel", source);
        Assert.Contains("CreateFramed", source);
    }

    [Fact]
    public void CallSites_UseAiGlyph_AndDoNotHardcodeTheMark()
    {
        string root = RepoRoot();
        foreach (string relative in CallSites)
        {
            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), relative);

            string source = relative.EndsWith("AssistantWindow.cs", StringComparison.Ordinal)
                ? string.Join("\n", Directory.GetFiles(Path.GetDirectoryName(path)!, "AssistantWindow*.cs").Select(File.ReadAllText))
                : File.ReadAllText(path);
            Assert.Contains("AiGlyph.Create", source);
            Assert.DoesNotContain("Text = \"AI\"", source);
        }
    }

    [Fact]
    public void HelpEntry_UsesSettingsNavigation()
    {
        string root = RepoRoot();
        string bar = File.ReadAllText(Path.Combine(
            root, "src/InkCanvas.Controls/Controls/FloatingBar.cs"));

        Assert.Contains("Glyphs.Ai", bar);
        string guide = File.ReadAllText(Path.Combine(root, "src/InkCanvas.App/Views/SettingsWindow.Guide.cs"));
        Assert.Contains("Glyphs.Help", guide);
        Assert.Contains("AddSubPage(\"关于与更新\", \"操作指南\"", guide);
    }
}
