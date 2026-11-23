using InkCanvas.Core.Settings;
using InkCanvas.Features.Classroom;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 课堂资源包回归：打包 / 解包往返、manifest 一致、原文件哈希不变、
/// 重复解包追加后缀不覆盖、功能开关关闭时拒绝打包、缺少 bundle.json 与打包失败清理。
/// 全部使用临时目录，测试结束递归清理。
/// </summary>
public sealed class ClassroomResourceBundleTests : IDisposable
{
    private const string ManifestJson = """
        {
          "name": "第 3 单元 一次函数",
          "files": ["lesson.txt", "notes.txt"]
        }
        """;

    private readonly string _root;

    public ClassroomResourceBundleTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "inkcanvas-bundle-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // 临时目录清理失败不影响测试结论；系统会在后续清理 %TEMP%。
        }
    }

    [Fact]
    public void ArchiveAndRestore_RoundTrip_PreservesContentAndSourceFiles()
    {
        string sourceDirectory = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceDirectory);
        string lesson = Path.Combine(sourceDirectory, "lesson.txt");
        string notes = Path.Combine(sourceDirectory, "notes.txt");
        File.WriteAllText(lesson, "一次函数：y = kx + b");
        File.WriteAllText(notes, "课堂板书：斜率与截距");

        string lessonHash = Sha256Of(lesson);
        string notesHash = Sha256Of(notes);

        string zipPath = Path.Combine(_root, "out", "lesson-bundle.zip");
        Assert.False(Directory.Exists(Path.GetDirectoryName(zipPath)), "打包前输出目录不应存在。");

        ClassroomResourceBundle.Archive(new[] { lesson, notes }, ManifestJson, zipPath);

        Assert.True(File.Exists(zipPath), "打包后应生成资源包。");
        Assert.True(Directory.Exists(Path.GetDirectoryName(zipPath)), "打包应自动创建输出目录。");

        string restoreDirectory = Path.Combine(_root, "restored");
        ClassroomBundleRestoreResult result = ClassroomResourceBundle.Restore(zipPath, restoreDirectory);

        Assert.Equal(2, result.Files.Count);
        Assert.Equal(ManifestJson, result.ManifestJson);

        string restoredLesson = Path.Combine(restoreDirectory, "lesson.txt");
        string restoredNotes = Path.Combine(restoreDirectory, "notes.txt");
        Assert.Contains(restoredLesson, result.Files);
        Assert.Contains(restoredNotes, result.Files);
        Assert.Equal(File.ReadAllText(lesson), File.ReadAllText(restoredLesson));
        Assert.Equal(File.ReadAllText(notes), File.ReadAllText(restoredNotes));

        Assert.Equal(lessonHash, Sha256Of(lesson));
        Assert.Equal(notesHash, Sha256Of(notes));
        Assert.Equal(2, Directory.GetFiles(sourceDirectory).Length);
    }

    [Fact]
    public void Restore_Twice_AppendsSuffixAndKeepsExistingFiles()
    {
        string sourceDirectory = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceDirectory);
        string lesson = Path.Combine(sourceDirectory, "lesson.txt");
        string notes = Path.Combine(sourceDirectory, "notes.txt");
        File.WriteAllText(lesson, "原始课件内容");
        File.WriteAllText(notes, "原始批注内容");

        string zipPath = Path.Combine(_root, "bundle.zip");
        ClassroomResourceBundle.Archive(new[] { lesson, notes }, ManifestJson, zipPath);

        string restoreDirectory = Path.Combine(_root, "restored");
        ClassroomBundleRestoreResult first = ClassroomResourceBundle.Restore(zipPath, restoreDirectory);
        ClassroomBundleRestoreResult second = ClassroomResourceBundle.Restore(zipPath, restoreDirectory);

        Assert.Equal(2, first.Files.Count);
        Assert.Equal(2, second.Files.Count);
        Assert.All(second.Files, path => Assert.Contains("-1", Path.GetFileName(path)));

        // 第二次解包不覆盖第一次的文件，内容保持一致。
        Assert.Equal(File.ReadAllText(lesson), File.ReadAllText(Path.Combine(restoreDirectory, "lesson.txt")));
        Assert.Equal(File.ReadAllText(lesson), File.ReadAllText(Path.Combine(restoreDirectory, "lesson-1.txt")));
        Assert.Equal(File.ReadAllText(notes), File.ReadAllText(Path.Combine(restoreDirectory, "notes-1.txt")));
        Assert.Equal(4, Directory.GetFiles(restoreDirectory).Length);
    }

    [Fact]
    public void Archive_WhenFeatureDisabled_Throws()
    {
        string sourceDirectory = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceDirectory);
        string lesson = Path.Combine(sourceDirectory, "lesson.txt");
        File.WriteAllText(lesson, "关闭开关时的内容");
        string zipPath = Path.Combine(_root, "disabled.zip");
        var settings = new FeatureSettings { EnableResourceBundle = false };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ClassroomResourceBundle.Archive(new[] { lesson }, ManifestJson, zipPath, settings));

        Assert.Equal("课堂资源包已关闭", error.Message);
        Assert.False(File.Exists(zipPath), "开关关闭时不应生成任何资源包。");
    }

    [Fact]
    public void Archive_WhenSourceMissing_CleansUpHalfProduct()
    {
        string sourceDirectory = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceDirectory);
        string missing = Path.Combine(sourceDirectory, "missing.txt");
        string zipPath = Path.Combine(_root, "broken", "partial.zip");

        Assert.Throws<FileNotFoundException>(
            () => ClassroomResourceBundle.Archive(new[] { missing }, ManifestJson, zipPath));

        Assert.False(File.Exists(zipPath), "打包失败不应留下正式资源包。");
        Assert.False(File.Exists(zipPath + ".tmp"), "打包失败应清理 .tmp 半成品。");
        Assert.True(File.Exists(missing) == false, "不存在的源文件不应被创建。");
    }

    [Fact]
    public void Restore_WithoutBundleManifest_ThrowsInvalidDataException()
    {
        string zipPath = Path.Combine(_root, "no-manifest.zip");
        using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = zip.CreateEntry("readme.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("没有 bundle.json 的旧包");
        }

        Assert.Throws<InvalidDataException>(
            () => ClassroomResourceBundle.Restore(zipPath, Path.Combine(_root, "restore-missing")));
    }

    /// <summary>
    /// 资源包扩展：白板 + 课件批注（manifest/按 slideId 的 .icink）+ 例题 + 录制全部入包，
    /// zip 内新增条目而 <c>bundle.json</c> 仍只含 version / createdAt / files / app。
    /// </summary>
    [Fact]
    public void Archive_CollectedSources_AddsAllCategoriesAndKeepsManifestShape()
    {
        string board = WriteSource("whiteboard/board.icink", "白板存档");
        string lessonRoot = Path.Combine(_root, "ppt");
        string lessonDir = Path.Combine(lessonRoot, "Auto Saved - Presentations", "函数_3");
        Directory.CreateDirectory(lessonDir);
        string manifest = Path.Combine(lessonDir, "InkCanvas-Ppt.json");
        File.WriteAllText(manifest,
            "{\"version\":2,\"slides\":[{\"slideId\":101,\"slideIndex\":1,\"file\":\"slide-101.icink\"}]}");
        string slide = Path.Combine(lessonDir, "slide-101.icink");
        File.WriteAllText(slide, "课件批注");
        string examples = WriteSource("examples/examples.json", "{\"version\":1,\"examples\":[]}");
        string recording = WriteSource("recordings/session-a.icink", "课堂录制");

        ClassroomBundleSourcePlan plan = ClassroomBundleSourceCollector.Collect(new ClassroomBundleSourceOptions
        {
            WhiteboardDirectory = Path.Combine(_root, "whiteboard"),
            PptAnnotationRoot = lessonRoot,
            ExampleLibraryDirectory = Path.Combine(_root, "examples"),
            RecordingDirectory = Path.Combine(_root, "recordings"),
        });

        Assert.False(plan.IsEmpty);
        Assert.Contains(plan.Files, file => file.SourcePath == board);
        Assert.Contains(plan.Files, file => file.SourcePath == manifest);
        Assert.Contains(plan.Files, file => file.SourcePath == slide);
        Assert.Contains(plan.Files, file => file.SourcePath == examples);
        Assert.Contains(plan.Files, file => file.SourcePath == recording);

        var paths = plan.Files.Select(file => file.SourcePath).ToList();
        IReadOnlyList<string> entryNames = ClassroomResourceBundle.PlanEntryNames(paths);
        string manifestJson = JsonSerializer.Serialize(new
        {
            version = 1,
            createdAt = "2026-11-23T00:00:00.0000000+08:00",
            files = entryNames,
            app = "InkCanvas",
        });

        string zipPath = Path.Combine(_root, "extended", "bundle.zip");
        ClassroomResourceBundle.Archive(paths, manifestJson, zipPath);

        using (ZipArchive zip = ZipFile.OpenRead(zipPath))
        {
            var entries = zip.Entries.Select(entry => entry.FullName).ToList();
            Assert.Contains(ClassroomResourceBundle.ManifestEntryName, entries);
            Assert.Equal(entryNames.Count + 1, entries.Count);
            Assert.All(entryNames, name => Assert.Contains(name, entries));

            ZipArchiveEntry manifestEntry = zip.GetEntry(ClassroomResourceBundle.ManifestEntryName)!;
            using var reader = new StreamReader(manifestEntry.Open());
            using JsonDocument document = JsonDocument.Parse(reader.ReadToEnd());
            JsonElement root = document.RootElement;

            // 结构保持不变：仍只有 version / createdAt / files / app 四个字段。
            Assert.Equal(4, root.EnumerateObject().Count());
            Assert.Equal(1, root.GetProperty("version").GetInt32());
            Assert.Equal("InkCanvas", root.GetProperty("app").GetString());
            Assert.True(root.TryGetProperty("createdAt", out _));
            Assert.Equal(entryNames,
                root.GetProperty("files").EnumerateArray().Select(item => item.GetString()).ToArray());
        }

        string restored = Path.Combine(_root, "restored-extended");
        ClassroomBundleRestoreResult result = ClassroomResourceBundle.Restore(zipPath, restored);

        Assert.Equal(entryNames.Count, result.Files.Count);
        Assert.Equal(manifestJson, result.ManifestJson);
        // 源文件只读：打包后哈希不变。
        Assert.Equal("白板存档", File.ReadAllText(board));
        Assert.Equal("课堂录制", File.ReadAllText(recording));
    }

    /// <summary>
    /// 真实目录四类来源（白板 / 课件批注 / 例题库 / 录制）→ 打包 → 解包到已有目录：
    /// 既有同名文件与同名目录必须保留，新文件追加后缀，全程无 .tmp 与 .restore-* 残留，
    /// 源文件内容与哈希保持不变。
    /// </summary>
    [Fact]
    public void RealDirectorySources_ArchiveThenRestore_NoOverwriteNoResidueAndSourceUnchanged()
    {
        string board = WriteSource("real/whiteboard/board.icink", "白板存档");
        string lessonRoot = Path.Combine(_root, "real", "ppt");
        string lessonDirectory = Path.Combine(lessonRoot, "Auto Saved - Presentations", "函数_3");
        Directory.CreateDirectory(lessonDirectory);
        string pptManifest = Path.Combine(lessonDirectory, "InkCanvas-Ppt.json");
        File.WriteAllText(pptManifest,
            "{\"version\":2,\"slides\":[{\"slideId\":101,\"slideIndex\":1,\"file\":\"slide-101.icink\"}]}");
        string slide = Path.Combine(lessonDirectory, "slide-101.icink");
        File.WriteAllText(slide, "课件批注");
        string examples = WriteSource("real/examples/examples.json", "{\"version\":1,\"examples\":[]}");
        string recording = WriteSource("real/recordings/session-a.icink", "课堂录制");

        ClassroomBundleSourcePlan plan = ClassroomBundleSourceCollector.Collect(new ClassroomBundleSourceOptions
        {
            WhiteboardDirectory = Path.Combine(_root, "real", "whiteboard"),
            PptAnnotationRoot = lessonRoot,
            ExampleLibraryDirectory = Path.Combine(_root, "real", "examples"),
            RecordingDirectory = Path.Combine(_root, "real", "recordings"),
        });

        Assert.Equal(4, plan.Files.Select(file => file.Kind).Distinct().Count());
        Assert.Equal(5, plan.Files.Count);
        var paths = plan.Files.Select(file => file.SourcePath).ToList();
        var sourceHashes = paths.ToDictionary(path => path, Sha256Of);

        IReadOnlyList<string> entryNames = ClassroomResourceBundle.PlanEntryNames(paths);
        string manifestJson = JsonSerializer.Serialize(new
        {
            version = 1,
            createdAt = "2026-11-23T00:00:00.0000000+08:00",
            files = entryNames,
            app = "InkCanvas",
        });

        string zipPath = Path.Combine(_root, "real-out", "bundle.zip");
        ClassroomResourceBundle.Archive(paths, manifestJson, zipPath);
        Assert.True(File.Exists(zipPath));

        // 目标目录预置同名文件与同名目录，解包不得覆盖。
        string restoreDirectory = Path.Combine(_root, "real-restored");
        Directory.CreateDirectory(restoreDirectory);
        string existingBoard = Path.Combine(restoreDirectory, "board.icink");
        File.WriteAllText(existingBoard, "既有白板文件");
        string existingManifestDirectory = Path.Combine(restoreDirectory, "InkCanvas-Ppt.json");
        Directory.CreateDirectory(existingManifestDirectory);

        ClassroomBundleRestoreResult result = ClassroomResourceBundle.Restore(zipPath, restoreDirectory);

        Assert.Equal(entryNames.Count, result.Files.Count);
        Assert.Equal(manifestJson, result.ManifestJson);
        Assert.Equal("既有白板文件", File.ReadAllText(existingBoard));
        Assert.True(Directory.Exists(existingManifestDirectory), "既有同名目录必须保留。");
        Assert.DoesNotContain(existingBoard, result.Files);
        Assert.Contains(result.Files, path => Path.GetFileName(path) == "board-1.icink");
        Assert.Contains(result.Files, path => Path.GetFileName(path) == "InkCanvas-Ppt-1.json");
        Assert.Equal("白板存档", File.ReadAllText(Path.Combine(restoreDirectory, "board-1.icink")));

        // 源文件只读：四类来源内容与哈希均不变。
        Assert.All(paths, path => Assert.Equal(sourceHashes[path], Sha256Of(path)));
        Assert.Equal("白板存档", File.ReadAllText(board));
        Assert.Equal("课件批注", File.ReadAllText(slide));
        Assert.Equal("{\"version\":1,\"examples\":[]}", File.ReadAllText(examples));
        Assert.Equal("课堂录制", File.ReadAllText(recording));

        Assert.Empty(Directory.GetDirectories(restoreDirectory, ".restore-*", SearchOption.TopDirectoryOnly));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    /// <summary>
    /// 真实目录四类来源收集完成后源文件被删除：打包必须回滚，
    /// 不留下正式 zip、.tmp 半成品或任何残留文件。
    /// </summary>
    [Fact]
    public void RealDirectorySources_ArchiveWhenCollectedFileDisappears_RollsBackWithoutResidue()
    {
        WriteSource("rollback/whiteboard/board.icink", "白板存档");
        string lessonRoot = Path.Combine(_root, "rollback", "ppt");
        string lessonDirectory = Path.Combine(lessonRoot, "Auto Saved - Presentations", "函数_3");
        Directory.CreateDirectory(lessonDirectory);
        File.WriteAllText(Path.Combine(lessonDirectory, "InkCanvas-Ppt.json"),
            "{\"version\":2,\"slides\":[{\"slideId\":101,\"slideIndex\":1,\"file\":\"slide-101.icink\"}]}");
        WriteSource("rollback/ppt/Auto Saved - Presentations/函数_3/slide-101.icink", "课件批注");
        WriteSource("rollback/examples/examples.json", "{\"version\":1,\"examples\":[]}");
        string recording = WriteSource("rollback/recordings/session-a.icink", "课堂录制");

        ClassroomBundleSourcePlan plan = ClassroomBundleSourceCollector.Collect(new ClassroomBundleSourceOptions
        {
            WhiteboardDirectory = Path.Combine(_root, "rollback", "whiteboard"),
            PptAnnotationRoot = lessonRoot,
            ExampleLibraryDirectory = Path.Combine(_root, "rollback", "examples"),
            RecordingDirectory = Path.Combine(_root, "rollback", "recordings"),
        });
        var paths = plan.Files.Select(file => file.SourcePath).ToList();
        Assert.Equal(4, plan.Files.Select(file => file.Kind).Distinct().Count());

        // 收集后删除最后一个来源，模拟打包中途源文件消失。
        File.Delete(recording);

        string zipPath = Path.Combine(_root, "rollback-out", "bundle.zip");
        Assert.Throws<FileNotFoundException>(
            () => ClassroomResourceBundle.Archive(paths, "{\"version\":1,\"files\":[],\"app\":\"InkCanvas\"}", zipPath));

        Assert.False(File.Exists(zipPath), "打包失败不应留下正式资源包。");
        Assert.False(File.Exists(zipPath + ".tmp"), "打包失败应清理 .tmp 半成品。");
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    /// <summary>解包成功或失败后临时目录都必须被清理。</summary>
    [Fact]
    public void Restore_LeavesNoTemporaryDirectory()
    {
        string lesson = WriteSource("source/lesson.txt", "临时目录");
        string zipPath = Path.Combine(_root, "temp-clean.zip");
        ClassroomResourceBundle.Archive(new[] { lesson }, ManifestJson, zipPath);

        string restoreDirectory = Path.Combine(_root, "restored-temp");
        ClassroomResourceBundle.Restore(zipPath, restoreDirectory);

        string[] leftovers = Directory.GetDirectories(restoreDirectory)
            .Where(path => Path.GetFileName(path).StartsWith(".restore-", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(leftovers);
    }

    [Fact]
    public void Restore_WithoutManifest_CleansUpNewlyCreatedTargetDirectory()
    {
        string zipPath = Path.Combine(_root, "no-manifest-cleanup.zip");
        using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("readme.txt").Open());
            writer.Write("缺少 bundle.json");
        }

        string target = Path.Combine(_root, "cleanup-target");
        Assert.Throws<InvalidDataException>(() => ClassroomResourceBundle.Restore(zipPath, target));
        Assert.False(Directory.Exists(target), "解包失败时应清理本次新建的空目标目录。");
    }

    /// <summary>目标目录中同名项是「目录」时也必须追加后缀，绝不覆盖或抛错。</summary>
    [Fact]
    public void Restore_WhenTargetNameIsDirectory_AppendsSuffixInsteadOfOverwriting()
    {
        string lesson = WriteSource("source/lesson.txt", "同名目录测试");
        string zipPath = Path.Combine(_root, "dir-collision.zip");
        ClassroomResourceBundle.Archive(new[] { lesson }, ManifestJson, zipPath);

        string restoreDirectory = Path.Combine(_root, "restored-dir-collision");
        Directory.CreateDirectory(Path.Combine(restoreDirectory, "lesson.txt"));

        ClassroomBundleRestoreResult result = ClassroomResourceBundle.Restore(zipPath, restoreDirectory);

        string written = Assert.Single(result.Files);
        Assert.Equal(Path.Combine(restoreDirectory, "lesson-1.txt"), written);
        Assert.True(Directory.Exists(Path.Combine(restoreDirectory, "lesson.txt")), "既有同名目录必须保留。");
        Assert.Equal("同名目录测试", File.ReadAllText(written));
    }

    private string WriteSource(string relativePath, string content)
    {
        string path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Sha256Of(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}