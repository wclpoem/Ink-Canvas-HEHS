using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 课堂资源包接入 UI 的静态契约：UI 层依赖 WinUI，无法在纯逻辑测试中实例化窗口，
/// 因此直接对源码断言关键调用存在，防止打包 / 解包入口、功能开关与单实例入口被后续改动丢失。
/// </summary>
public sealed class ResourceBundleUiContractTests
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
    public void ResourceBundleWindow_UsesBundleServiceAndFeatureGate()
    {
        string source = ReadSource("src/InkCanvas.App/Views/ResourceBundleWindow.cs");

        Assert.Contains("ClassroomResourceBundle.Archive", source);
        Assert.Contains("ClassroomResourceBundle.Restore", source);
        Assert.Contains("EnableResourceBundle", source);
        Assert.Contains(".icink", source);
        Assert.DoesNotContain("ClassroomSessionStore", source);
        Assert.Contains("课堂资源包", source);
        Assert.Contains("课堂资源包已关闭", source);
        Assert.Contains("暂无可打包的板书存档", source);
    }

    [Fact]
    public void ResourceBundleWindow_PacksManifestAndRestoresIntoBundlesDirectory()
    {
        string source = ReadSource("src/InkCanvas.App/Views/ResourceBundleWindow.cs");

        Assert.Contains("version = 1", source);
        Assert.Contains("createdAt", source);
        Assert.Contains("app = \"InkCanvas\"", source);
        Assert.Contains("\"bundles\"", source);
        Assert.Contains("yyyyMMdd-HHmmss", source);
        Assert.Contains("ShowWindow", source);
    }

    [Fact]
    public void ToolkitWindow_ExposesResourceBundleEntry()
    {
        string source = ReadSource("src/InkCanvas.App/Views/ToolkitWindow.cs");

        Assert.Contains("资源包", source);
        Assert.Contains("_openResourceBundle", source);
    }

    [Fact]
    public void ShellController_ExposesSingleInstanceResourceBundleEntry()
    {
        string source = ReadSource("src/InkCanvas.App/Services/ShellController.cs");

        Assert.Contains("public void OpenResourceBundle()", source);
        Assert.Contains("new ResourceBundleWindow(", source);
        Assert.Contains("Services.Settings.Features", source);
        Assert.Contains("_resourceBundle", source);
    }
}