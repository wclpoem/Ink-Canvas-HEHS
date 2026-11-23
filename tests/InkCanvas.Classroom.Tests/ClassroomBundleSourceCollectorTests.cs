using InkCanvas.Features.Classroom;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 课堂资源包来源收集：白板存档、课件批注（manifest + 登记的 slide .icink）、
/// 例题库、插件录制的分类收集、去重与数量/大小上限。全部使用临时目录，不触碰真实课堂数据。
/// </summary>
public sealed class ClassroomBundleSourceCollectorTests : IDisposable
{
    private readonly string _root;

    public ClassroomBundleSourceCollectorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "inkcanvas-bundle-sources", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论。 */ }
    }

    private string CreateFile(string relativePath, string content)
    {
        string path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string CreatePptDirectory(string lessonsRoot, string lessonName,
        string manifestSlidesJson, params string[] slideFiles)
    {
        string directory = Path.Combine(lessonsRoot, "Auto Saved - Presentations", lessonName);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "InkCanvas-Ppt.json"),
            "{\"version\":2,\"lastSlideIndex\":2,\"slides\":" + manifestSlidesJson + "}");
        foreach (string slide in slideFiles)
            File.WriteAllText(Path.Combine(directory, slide), "slide " + slide);
        return directory;
    }

    private ClassroomBundleSourceOptions FullOptions() => new()
    {
        WhiteboardDirectory = Path.Combine(_root, "whiteboard"),
        PptAnnotationRoot = Path.Combine(_root, "ppt"),
        ExampleLibraryDirectory = Path.Combine(_root, "examples"),
        RecordingDirectory = Path.Combine(_root, "recordings"),
    };

    [Fact]
    public void Collect_GathersAllCategories_InDeterministicOrder()
    {
        CreateFile("whiteboard/board-a.icink", "board-a");
        CreateFile("whiteboard/board-b.icink", "board-b");
        CreateFile("whiteboard/notes.txt", "ignored");

        CreatePptDirectory(Path.Combine(_root, "ppt"), "函数_3",
            "[{\"slideId\":101,\"slideIndex\":1,\"file\":\"slide-101.icink\"}," +
            "{\"slideId\":102,\"slideIndex\":2,\"file\":\"slide-102.icink\"}]",
            "slide-101.icink", "slide-102.icink", "slide-103.icink");

        CreateFile("examples/examples.json", "{\"version\":1,\"examples\":[]}");
        CreateFile("recordings/session-a.icink", "recording-a");
        CreateFile("recordings/session-b.icink", "recording-b");
        CreateFile("recordings/session-a.icink.tmp", "ignored");

        ClassroomBundleSourcePlan plan = ClassroomBundleSourceCollector.Collect(FullOptions());

        Assert.False(plan.Truncated);
        Assert.Equal(2, plan.Files.Count(file => file.Kind == ClassroomBundleSourceKind.Whiteboard));
        Assert.Equal(3, plan.Files.Count(file => file.Kind == ClassroomBundleSourceKind.PptAnnotation));
        Assert.Single(plan.Files, file => file.Kind == ClassroomBundleSourceKind.ExampleLibrary);
        Assert.Equal(2, plan.Files.Count(file => file.Kind == ClassroomBundleSourceKind.Recording));

        // 类别顺序：白板 → 课件批注（manifest 在前）→ 例题 → 录制。
        Assert.Equal(ClassroomBundleSourceKind.Whiteboard, plan.Files[0].Kind);
        int manifestIndex = plan.Files.ToList().FindIndex(
            file => Path.GetFileName(file.SourcePath) == "InkCanvas-Ppt.json");
        Assert.True(manifestIndex > 1, "manifest 应随课件批注在 slide 之前收集。");
        Assert.Equal("InkCanvas-Ppt.json", Path.GetFileName(plan.Files[manifestIndex].SourcePath));

        // manifest 未登记的 slide-103.icink 不进包。
        Assert.DoesNotContain(plan.Files,
            file => Path.GetFileName(file.SourcePath).Equals("slide-103.icink", StringComparison.OrdinalIgnoreCase));

        // .tmp 与非 .icink 文件不进入录制候选。
        Assert.DoesNotContain(plan.Files,
            file => file.SourcePath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Files,
            file => Path.GetFileName(file.SourcePath).Equals("notes.txt", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(plan.Files.Sum(file => file.SizeBytes), plan.TotalBytes);
        Assert.All(plan.Files, file => Assert.True(File.Exists(file.SourcePath)));
    }

    [Fact]
    public void Collect_EnforcesFileCountLimit_AndReportsTruncation()
    {
        for (int i = 0; i < 6; i++)
            CreateFile($"whiteboard/board-{i}.icink", "x");

        ClassroomBundleSourceOptions options = FullOptions() with { MaxFiles = 3 };
        ClassroomBundleSourcePlan plan = ClassroomBundleSourceCollector.Collect(options);

        Assert.Equal(3, plan.Files.Count);
        Assert.Equal(3, plan.SkippedByLimit);
        Assert.True(plan.Truncated);
    }

    [Fact]
    public void Collect_EnforcesTotalSizeAndSingleFileLimits()
    {
        CreateFile("whiteboard/small.icink", "0123456789");
        CreateFile("whiteboard/medium.icink", new string('m', 20));
        CreateFile("whiteboard/large.icink", new string('x', 200));
        CreateFile("recordings/session.icink", new string('y', 300));

        ClassroomBundleSourceOptions options = FullOptions() with
        {
            MaxFileBytes = 128,
            MaxTotalBytes = 15,
        };
        ClassroomBundleSourcePlan plan = ClassroomBundleSourceCollector.Collect(options);

        Assert.Single(plan.Files);
        Assert.Equal("small.icink", Path.GetFileName(plan.Files[0].SourcePath));
        // large.icink 与录制文件超过单文件上限（2 个），medium.icink 被总大小上限跳过（1 个）。
        Assert.Equal(3, plan.SkippedBySize);
        Assert.True(plan.Truncated);
    }

    [Fact]
    public void Collect_DeduplicatesSameSourcePathAcrossCategories()
    {
        CreateFile("whiteboard/shared.icink", "shared");
        ClassroomBundleSourceOptions options = FullOptions() with
        {
            WhiteboardDirectory = Path.Combine(_root, "whiteboard"),
            RecordingDirectory = Path.Combine(_root, "whiteboard"),
        };

        ClassroomBundleSourcePlan plan = ClassroomBundleSourceCollector.Collect(options);

        Assert.Single(plan.Files, file => Path.GetFileName(file.SourcePath) == "shared.icink");
        Assert.Equal(ClassroomBundleSourceKind.Whiteboard, plan.Files[0].Kind);
    }
}