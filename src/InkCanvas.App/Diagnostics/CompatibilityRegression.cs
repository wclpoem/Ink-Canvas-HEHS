using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InkCanvas.App.Services;
using InkCanvas.Core.Classroom;
using InkCanvas.Core.Diagnostics;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Board;
using InkCanvas.Features.Board.AiObjects;
using InkCanvas.Features.Classroom;
using InkCanvas.Features.Ppt;
using Microsoft.UI.Xaml;

namespace InkCanvas.App.Diagnostics;

/// <summary>
/// 兼容性运行时诊断（<c>--compatcheck=&lt;dir&gt;</c>）：用本机可构造的 fixture 验证
/// v3/v2 画板迁移、schema=2 会话回放、主/子板录制合并、资源包真实打包/解包与无 PPT 讲义导出。
/// 全部使用临时目录与合成数据，不联网、不写真实课件；报告输出 <c>compat-check.txt/json</c>，
/// 任一项失败时进程退出码为 1。
/// </summary>
internal static class CompatibilityRegression
{
    private const string LogPrefix = "[Compat][CompatCheck]";
    private const long FixedCreatedAt = 1_730_000_000_000;

    private const string OnePixelPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private sealed record CheckItem(string Check, bool Passed, bool Optional, double ElapsedMs,
        Dictionary<string, object> Details);

    private sealed class CheckLog
    {
        private readonly List<CheckItem> _items = new();
        private readonly Stopwatch _phase = Stopwatch.StartNew();

        public void Add(string check, bool passed, params (string Key, object Value)[] details)
            => AddCore(check, passed, optional: false, details);

        public void AddOptional(string check, bool passed, params (string Key, object Value)[] details)
            => AddCore(check, passed, optional: true, details);

        private void AddCore(string check, bool passed, bool optional, (string Key, object Value)[] details)
        {
            var values = new Dictionary<string, object>();
            foreach (var (key, value) in details) values[key] = value;
            _items.Add(new CheckItem(check, passed, optional, Math.Round(_phase.Elapsed.TotalMilliseconds, 1), values));
            _phase.Restart();
            FileLog.WriteLogToFile(
                $"{LogPrefix} {(passed ? "PASS" : "FAIL")} {check}",
                passed ? LogType.Info : LogType.Error);
        }

        public IReadOnlyList<CheckItem> Items => _items;

        public bool Passed => _items.All(item => item.Passed || item.Optional);
    }

    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var log = new CheckLog();
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        string textPath = Path.Combine(directory, "compat-check.txt");
        string jsonPath = Path.Combine(directory, "compat-check.json");
        FileLog.WriteLogToFile($"{LogPrefix} 开始，输出目录：{directory}", LogType.Info);

        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        services.Settings.Automation.AutoSavedStrokesLocation = Path.Combine(directory, "captures");
        using var shell = new ShellController(services);

        try
        {
            shell.Start();
            shell.StartInClickThrough();
            await Task.Delay(250);

            await CheckV3MigrationAsync(log, directory);
            await CheckV2LegacyAsync(log, directory);
            CheckSchema2Payload(log);
            CheckSideBoardRecordingMerge(log, directory);
            CheckResourceBundle(log, directory);
            await CheckHandoutExportAsync(log, directory);

            log.Add("compatcheck completed without exception", true);
        }
        catch (Exception ex)
        {
            log.Add("compatcheck exception", false, ("error", ex.ToString()));
        }
        finally
        {
            try { shell.Dispose(); } catch { /* 清理失败不影响结果 */ }
            CleanupTemporaryDirectories(directory);
            WriteReport(textPath, jsonPath, log, jsonOptions);
            bool passed = log.Passed;
            FileLog.WriteLogToFile(
                $"{LogPrefix} 完成：{log.Items.Count(item => item.Passed)}/{log.Items.Count} 通过，退出码 {(passed ? 0 : 1)}",
                passed ? LogType.Info : LogType.Error);
            Environment.ExitCode = passed ? 0 : 1;
            Application.Current.Exit();
        }
    }

    // ---- 1. v3 画板迁移 -----------------------------------------------------

    private static async Task CheckV3MigrationAsync(CheckLog log, string directory)
    {
        const string check = "v3 board package migrates to v4 (board/page/stroke/image, PageId generated)";
        try
        {
            var main = new BoardService();
            var side = new BoardService();
            main.SideBoardSource = side;

            main.Current.Document.Add(SyntheticStroke(10, 20));
            main.AddImagesAtomic(new[] { TestImage("主图", 30, 40) }, "兼容主图");
            main.AddAiObjectsAtomic(
                new[] { TestText("主文本", 10, 120) },
                new[] { TestFormula("x^2", 10, 240) },
                "兼容主对象", "main-group");

            side.Current.Document.Add(SyntheticStroke(60, 70));
            side.AddImagesAtomic(new[] { TestImage("子图", 80, 100) }, "兼容子图");
            side.AddAiObjectsAtomic(
                new[] { TestText("子文本", 20, 20) },
                new[] { TestFormula("a^2+b^2", 20, 140) },
                "兼容子对象", "side-group");

            string v4Path = Path.Combine(directory, "compat-v4.icink");
            await main.SaveBoardAsync(v4Path);
            int v4Version = ReadBoardVersion(v4Path);

            string v3Path = Path.Combine(directory, "compat-v3.icink");
            DowngradeToV3(v4Path, v3Path, Path.Combine(directory, "v3-work"));
            int v3Version = ReadBoardVersion(v3Path);
            bool downgraded = InspectV3Manifest(v3Path);

            var loaded = new BoardService();
            var loadedSide = new BoardService();
            loaded.SideBoardSource = loadedSide;
            await loaded.LoadBoardAsync(v3Path);

            bool loadedOk = loaded.PageCount == 1
                && loaded.Current.Document.Count == 1
                && loaded.CurrentImages.Count == 1
                && loaded.CurrentImages[0].Data is { Length: > 0 }
                // v3 移除 TextObjects/FormulaObjects：加载后按缺省空集合处理。
                && loaded.CurrentTextObjects.Count == 0
                && loaded.CurrentFormulaObjects.Count == 0
                && loadedSide.Current.Document.Count == 1
                && loadedSide.CurrentImages.Count == 1
                && loadedSide.CurrentImages[0].Data is { Length: > 0 }
                && loadedSide.CurrentTextObjects.Count == 0
                && loadedSide.CurrentFormulaObjects.Count == 0;

            // 缺失 PageId 运行期生成，不能是 Guid.Empty。
            bool pageIdGenerated = loaded.ResolvePageId(0) != Guid.Empty
                && loadedSide.ResolvePageId(0) != Guid.Empty;

            string rewritten = Path.Combine(directory, "compat-v3-rewritten.icink");
            await loaded.SaveBoardAsync(rewritten);
            int rewrittenVersion = ReadBoardVersion(rewritten);
            bool rewrittenV4 = rewrittenVersion == 4
                && InspectPageId(rewritten, "Pages")
                && InspectPageId(rewritten, "SideBoard");

            log.Add(check,
                v4Version == 4 && v3Version == 3 && downgraded && loadedOk && pageIdGenerated && rewrittenV4,
                ("v4Version", v4Version), ("v3Version", v3Version), ("v3FieldsRemoved", downgraded),
                ("mainStrokes", loaded.Current.Document.Count), ("mainImages", loaded.CurrentImages.Count),
                ("sideStrokes", loadedSide.Current.Document.Count), ("sideImages", loadedSide.CurrentImages.Count),
                ("textsAfterV3", loaded.CurrentTextObjects.Count + loadedSide.CurrentTextObjects.Count),
                ("formulasAfterV3", loaded.CurrentFormulaObjects.Count + loadedSide.CurrentFormulaObjects.Count),
                ("pageId", loaded.ResolvePageId(0).ToString("N")),
                ("rewrittenVersion", rewrittenVersion));
        }
        catch (Exception ex)
        {
            log.Add(check, false, ("error", ex.Message));
        }
    }

    private static void DowngradeToV3(string source, string target, string workRoot)
    {
        if (Directory.Exists(workRoot)) Directory.Delete(workRoot, recursive: true);
        Directory.CreateDirectory(workRoot);
        ZipFile.ExtractToDirectory(source, workRoot);

        foreach (string name in new[] { "board.json", "board.pretty.json" })
        {
            string path = Path.Combine(workRoot, name);
            JsonNode root = JsonNode.Parse(File.ReadAllText(path))
                ?? throw new InvalidDataException("板包清单为空：" + name);
            root["Version"] = 3;
            RemoveV4PageFields(root["Pages"] as JsonArray);
            if (root["SideBoard"] is JsonObject side)
                RemoveV4PageFields(side["Pages"] as JsonArray);

            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = name.Contains("pretty", StringComparison.OrdinalIgnoreCase),
            }));
        }

        if (File.Exists(target)) File.Delete(target);
        ZipFile.CreateFromDirectory(workRoot, target, CompressionLevel.Fastest, includeBaseDirectory: false);
    }

    private static void RemoveV4PageFields(JsonArray pages)
    {
        if (pages is null) return;
        foreach (JsonNode page in pages)
        {
            if (page is not JsonObject item) continue;
            item.Remove("PageId");
            item.Remove("TextObjects");
            item.Remove("FormulaObjects");
        }
    }

    private static bool InspectV3Manifest(string path)
    {
        using ZipArchive archive = ZipFile.OpenRead(path);
        ZipArchiveEntry manifest = archive.GetEntry("board.json")
            ?? throw new InvalidDataException("v3 包缺少 board.json。");
        using var reader = new StreamReader(manifest.Open());
        JsonNode root = JsonNode.Parse(reader.ReadToEnd())!;
        if (root["Version"]?.GetValue<int>() != 3) return false;
        return PagesHaveNoV4Fields(root["Pages"] as JsonArray)
            && (root["SideBoard"] is not JsonObject side || PagesHaveNoV4Fields(side["Pages"] as JsonArray));
    }

    private static bool PagesHaveNoV4Fields(JsonArray pages)
    {
        if (pages is null) return true;
        foreach (JsonNode page in pages)
        {
            if (page is not JsonObject item) continue;
            if (item.ContainsKey("PageId") || item.ContainsKey("TextObjects") || item.ContainsKey("FormulaObjects"))
                return false;
        }
        return true;
    }

    private static bool InspectPageId(string path, string scope)
    {
        using ZipArchive archive = ZipFile.OpenRead(path);
        ZipArchiveEntry manifest = archive.GetEntry("board.json")!;
        using var reader = new StreamReader(manifest.Open());
        JsonNode root = JsonNode.Parse(reader.ReadToEnd())!;
        JsonArray pages = scope == "Pages"
            ? root["Pages"] as JsonArray
            : (root["SideBoard"] as JsonObject)?["Pages"] as JsonArray;
        if (pages is null || pages.Count == 0) return false;
        string? id = pages[0]?["PageId"]?.GetValue<string>();
        return !string.IsNullOrWhiteSpace(id) && Guid.TryParse(id, out Guid parsed) && parsed != Guid.Empty;
    }

    // ---- 2. v2 旧单页 JSON --------------------------------------------------

    private static async Task CheckV2LegacyAsync(CheckLog log, string directory)
    {
        const string check = "v2 legacy single-page JSON loads strokes without ZIP header";
        try
        {
            var board = new BoardService();
            board.Current.Document.Add(SyntheticStroke(15, 25));
            string v4Path = Path.Combine(directory, "compat-v2-source.icink");
            await board.SaveBoardAsync(v4Path);

            JsonArray strokes;
            using (ZipArchive archive = ZipFile.OpenRead(v4Path))
            {
                using var reader = new StreamReader(archive.GetEntry("board.json")!.Open());
                JsonNode root = JsonNode.Parse(reader.ReadToEnd())!;
                strokes = root["Pages"]![0]!["Strokes"]!.DeepClone().AsArray();
            }

            string legacyPath = Path.Combine(directory, "compat-v2-legacy.json");
            File.WriteAllText(legacyPath, new JsonObject { ["Strokes"] = strokes }.ToJsonString());

            var loaded = new BoardService();
            Action apply = await loaded.PrepareOpenAsync(legacyPath, entireBoard: false);
            apply();

            bool noZipHeader = !HasZipSignature(legacyPath);
            bool loadedOk = loaded.PageCount == 1
                && loaded.Current.Document.Count == 1
                && loaded.CurrentImages.Count == 0;

            log.Add(check, noZipHeader && loadedOk,
                ("zipHeader", HasZipSignature(legacyPath)),
                ("pages", loaded.PageCount), ("strokes", loaded.Current.Document.Count),
                ("images", loaded.CurrentImages.Count), ("path", "compat-v2-legacy.json"));
        }
        catch (Exception ex)
        {
            log.Add(check, false, ("error", ex.Message));
        }
    }

    private static bool HasZipSignature(string path)
    {
        using FileStream stream = File.OpenRead(path);
        Span<byte> signature = stackalloc byte[4];
        return stream.Read(signature) == 4 && signature[0] == 'P' && signature[1] == 'K';
    }

    // ---- 3. schema=2 会话载荷 ------------------------------------------------

    private static void CheckSchema2Payload(CheckLog log)
    {
        const string check = "schema=2 AI payload decodes with identity transform and no throw";
        try
        {
            string payload = """
                {
                  "objectId": "legacy-image",
                  "action": "add",
                  "schema": 2,
                  "pageIndex": 0,
                  "boardId": 0,
                  "objects": [
                    { "id": "legacy-image", "kind": "image", "left": 10, "top": 10,
                      "width": 100, "height": 50, "text": "旧 PNG 卡片",
                      "renderPngBase64": "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==" }
                  ],
                  "nativeObjects": [
                    { "id": "legacy-native", "type": "text", "content": "旧原生文本",
                      "left": 20, "top": 70, "width": 120, "height": 40 }
                  ]
                }
                """;

            bool decoded = AiObjectPayloadCodec.TryDecode(payload, out AiObjectsPayload result);
            var session = new ClassroomSession
            {
                Id = "compat-schema2",
                Name = "schema2",
                CreatedAt = DateTime.Now,
                Events = new List<TimelineEvent>
                {
                    new()
                    {
                        SessionId = "compat-schema2",
                        Seq = 1,
                        TimeMs = 100,
                        PageIndex = 0,
                        BoardId = 0,
                        Kind = TimelineEventKind.AiStep,
                        Payload = payload,
                    },
                },
                Checkpoints = new List<SessionCheckpoint>(),
            };

            IReadOnlyList<TimelineAiObject> frame = new InkTimeline(session).BuildObjectFrame(150);
            TimelineAiObject? native = frame.FirstOrDefault(item => item.Id == "legacy-native");
            TimelineAiObject? image = frame.FirstOrDefault(item => item.Id == "legacy-image");
            bool fieldsOk = decoded
                && result.Objects.Count == 1 && result.NativeObjects.Count == 1
                && frame.Count == 2
                && native is not null && image is not null
                && native.Transform is null && !native.HasTransform && native.Source == "" && native.SlideId is null
                && image!.Transform is null && !image.HasTransform && image.Source == "" && image.SlideId is null
                && native.FontFamily == "Microsoft YaHei UI" && Math.Abs(native.FontSize - 32) < 0.01
                && native.Color == "#FF202024" && Math.Abs(native.LineHeight - 1.4) < 0.01
                && native.Align == "left" && native.Columns == 1;

            // schema=2 缺 transform 时原生元素按 identity 回放，且不抛异常。
            bool identity = native is not null
                && new TimelineNativeTextElement(native).Frame.Transform == Matrix3x2.Identity;

            log.Add(check, fieldsOk && identity,
                ("decoded", decoded), ("objects", result?.Objects.Count ?? -1),
                ("nativeObjects", result?.NativeObjects.Count ?? -1), ("frame", frame.Count),
                ("nativeTransform", native?.Transform is null ? "identity" : "present"),
                ("identityFrame", identity),
                ("nativeSource", native?.Source ?? ""), ("nativeSlideId", native?.SlideId?.ToString() ?? "null"));
        }
        catch (Exception ex)
        {
            log.Add(check, false, ("error", ex.Message));
        }
    }

    // ---- 4. 主/子板录制合并回放 ---------------------------------------------

    private static void CheckSideBoardRecordingMerge(CheckLog log, string directory)
    {
        const string check = "main/side recorder merge replays strokes and AI objects per board without network";
        try
        {
            string sessions = Path.Combine(directory, "sessions");
            Directory.CreateDirectory(sessions);

            var mainBoard = new BoardService();
            var sideBoard = new BoardService();
            InkStroke mainStroke = SyntheticStroke(10, 10);
            InkStroke sideStroke = SyntheticStroke(210, 210);

            using var mainRecorder = new InkRecorder(mainBoard, boardId: 0, persistOnStop: false)
            {
                SessionDirectory = sessions,
            };
            using var sideRecorder = new InkRecorder(sideBoard, boardId: 1, persistOnStop: false)
            {
                SessionDirectory = sessions,
            };

            mainRecorder.Start("兼容主录制");
            sideRecorder.Start("兼容子录制");

            mainRecorder.Append(new TimelineEvent
            {
                TimeMs = 100, PageIndex = 0, BoardId = 0, Kind = TimelineEventKind.StrokeAdded,
                StrokeId = mainStroke.Id.ToString(), StrokeJson = StrokeJsonCodec.Serialize(mainStroke),
            });
            sideRecorder.Append(new TimelineEvent
            {
                TimeMs = 120, PageIndex = 0, BoardId = 1, Kind = TimelineEventKind.StrokeAdded,
                StrokeId = sideStroke.Id.ToString(), StrokeJson = StrokeJsonCodec.Serialize(sideStroke),
            });

            mainBoard.AddAiObjectsAtomic(
                new[] { TestText("主板 AI 文本", 10, 300) },
                Array.Empty<BoardFormulaObject>(), "AI 主文本", "main-group");
            sideBoard.AddAiObjectsAtomic(
                new[] { TestText("子板 AI 文本", 20, 320) },
                Array.Empty<BoardFormulaObject>(), "AI 子文本", "side-group");

            sideRecorder.Stop();
            ClassroomSession sideSession = sideRecorder.Session;
            mainRecorder.MergeEvents(sideSession.Events);
            mainRecorder.Stop();

            ClassroomSession merged = mainRecorder.Session;
            List<TimelineEvent> strokeEvents = merged.Events
                .Where(item => item.Kind == TimelineEventKind.StrokeAdded).ToList();
            List<TimelineEvent> aiEvents = merged.Events
                .Where(item => item.Kind == TimelineEventKind.AiStep).ToList();
            HashSet<int> strokeBoards = strokeEvents.Select(item => item.BoardId).ToHashSet();
            HashSet<int> aiBoards = aiEvents.Select(item => item.BoardId).ToHashSet();

            var timeline = new InkTimeline(merged);
            long at = Math.Max(merged.Events[^1].TimeMs, 200);
            string mainSignature = StrokeSignature(mainStroke);
            string sideSignature = StrokeSignature(sideStroke);

            IReadOnlyList<InkStroke> mainFrame = timeline.BuildFrame(at, 0);
            IReadOnlyList<InkStroke> sideFrame = timeline.BuildFrame(at, 1);
            bool strokeSplit = mainFrame.Count == 1 && StrokeSignature(mainFrame[0]) == mainSignature
                && sideFrame.Count == 1 && StrokeSignature(sideFrame[0]) == sideSignature;

            IReadOnlyList<TimelineAiObject> mainObjects = timeline.BuildObjectFrame(at, 0);
            IReadOnlyList<TimelineAiObject> sideObjects = timeline.BuildObjectFrame(at, 1);
            bool objectSplit = mainObjects.Count == 1 && mainObjects[0].Content == "主板 AI 文本"
                && sideObjects.Count == 1 && sideObjects[0].Content == "子板 AI 文本";

            // 回放只读：事件载荷与对象 PNG 不变化；Core 程序集无网络/AI 插件引用。
            string before = string.Join("|", merged.Events.Select(item => item.Payload));
            _ = timeline.BuildFrame(at, 0);
            _ = timeline.BuildFrame(at, 1);
            _ = timeline.BuildObjectFrame(at, 0);
            _ = timeline.BuildObjectFrame(at, 1);
            bool readOnly = before == string.Join("|", merged.Events.Select(item => item.Payload));
            var references = typeof(InkTimeline).Assembly.GetReferencedAssemblies()
                .Select(reference => reference.Name).ToArray();
            bool isolated = !references.Contains("System.Net.Http")
                && !references.Contains("InkCanvas.Platform")
                && !references.Contains("InkCanvas.Modules.AiAssistant");

            log.Add(check,
                strokeBoards.SetEquals(new[] { 0, 1 }) && aiBoards.SetEquals(new[] { 0, 1 })
                    && strokeSplit && objectSplit && readOnly && isolated,
                ("strokeEvents", strokeEvents.Count), ("strokeBoards", string.Join(",", strokeBoards.Order())),
                ("aiEvents", aiEvents.Count), ("aiBoards", string.Join(",", aiBoards.Order())),
                ("mainStrokes", mainFrame.Count), ("sideStrokes", sideFrame.Count),
                ("mainObjects", mainObjects.Count), ("sideObjects", sideObjects.Count),
                ("readOnly", readOnly), ("networkReferences", isolated ? "none" : "present"));
        }
        catch (Exception ex)
        {
            log.Add(check, false, ("error", ex.Message));
        }
    }

    // ---- 5. 资源包 ----------------------------------------------------------

    private static void CheckResourceBundle(CheckLog log, string directory)
    {
        const string check = "resource bundle archives/restores four categories without overwrite";
        try
        {
            string fixture = Path.Combine(directory, "bundle-fixture");
            string whiteboard = WriteFixture(Path.Combine(fixture, "whiteboard", "board.icink"), "白板存档");
            string lessonDir = Path.Combine(fixture, "ppt", "Auto Saved - Presentations", "函数_3");
            Directory.CreateDirectory(lessonDir);
            string pptManifest = Path.Combine(lessonDir, "InkCanvas-Ppt.json");
            File.WriteAllText(pptManifest,
                "{\"version\":2,\"slides\":[{\"slideId\":101,\"slideIndex\":1,\"file\":\"slide-101.icink\"}]}");
            string slide = WriteFixture(Path.Combine(lessonDir, "slide-101.icink"), "课件批注");
            string examples = WriteFixture(Path.Combine(fixture, "examples", "examples.json"),
                "{\"version\":1,\"examples\":[]}");
            string recording = WriteFixture(Path.Combine(fixture, "recordings", "session-a.icink"), "课堂录制");

            ClassroomBundleSourcePlan plan = ClassroomBundleSourceCollector.Collect(new ClassroomBundleSourceOptions
            {
                WhiteboardDirectory = Path.Combine(fixture, "whiteboard"),
                PptAnnotationRoot = Path.Combine(fixture, "ppt"),
                ExampleLibraryDirectory = Path.Combine(fixture, "examples"),
                RecordingDirectory = Path.Combine(fixture, "recordings"),
            });

            var paths = plan.Files.Select(file => file.SourcePath).ToList();
            IReadOnlyList<string> entryNames = ClassroomResourceBundle.PlanEntryNames(paths);
            string manifestJson = JsonSerializer.Serialize(new
            {
                version = 1,
                createdAt = "2026-11-23T00:00:00.0000000+08:00",
                files = entryNames,
                app = "InkCanvas",
            }, new JsonSerializerOptions { WriteIndented = true });

            string zipPath = Path.Combine(directory, "compat-bundle.zip");
            ClassroomResourceBundle.Archive(paths, manifestJson, zipPath);

            bool categoriesOk = plan.Files.Count(file => file.Kind == ClassroomBundleSourceKind.Whiteboard) == 1
                && plan.Files.Count(file => file.Kind == ClassroomBundleSourceKind.PptAnnotation) == 2
                && plan.Files.Count(file => file.Kind == ClassroomBundleSourceKind.ExampleLibrary) == 1
                && plan.Files.Count(file => file.Kind == ClassroomBundleSourceKind.Recording) == 1;

            bool manifestShape;
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                using var reader = new StreamReader(archive.GetEntry("bundle.json")!.Open());
                JsonNode manifest = JsonNode.Parse(reader.ReadToEnd())!;
                manifestShape = manifest.AsObject().Count == 4
                    && manifest["version"]?.GetValue<int>() == 1
                    && manifest["app"]?.GetValue<string>() == "InkCanvas"
                    && manifest["files"]!.AsArray().Count == entryNames.Count;
            }

            string restoreDirectory = Path.Combine(directory, "bundle-restored");
            ClassroomBundleRestoreResult first = ClassroomResourceBundle.Restore(zipPath, restoreDirectory);
            string firstBoard = Path.Combine(restoreDirectory, "board.icink");
            string firstContent = File.ReadAllText(firstBoard);
            ClassroomBundleRestoreResult second = ClassroomResourceBundle.Restore(zipPath, restoreDirectory);
            bool noOverwrite = first.Files.Count == plan.Files.Count
                && second.Files.Count == plan.Files.Count
                && File.ReadAllText(firstBoard) == firstContent
                && second.Files.All(path => Path.GetFileName(path).Contains("-1", StringComparison.Ordinal));

            bool noRestoreTemp = Directory.GetDirectories(restoreDirectory)
                .All(path => !Path.GetFileName(path).StartsWith(".restore-", StringComparison.Ordinal));

            // 失败清理：缺少 bundle.json 的包必须抛错，且本次新建的空目标目录与临时目录都被清理。
            string brokenZip = Path.Combine(directory, "compat-bundle-broken.zip");
            using (var stream = new FileStream(brokenZip, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(archive.CreateEntry("readme.txt").Open());
                writer.Write("缺少 bundle.json");
            }

            string brokenTarget = Path.Combine(directory, "bundle-broken");
            bool failureCleanup = false;
            try
            {
                ClassroomResourceBundle.Restore(brokenZip, brokenTarget);
            }
            catch (InvalidDataException)
            {
                failureCleanup = !Directory.Exists(brokenTarget);
            }

            // 源文件只读。
            bool sourceReadOnly = File.ReadAllText(whiteboard) == "白板存档"
                && File.ReadAllText(pptManifest).Contains("\"slideId\":101")
                && File.ReadAllText(slide) == "课件批注"
                && File.ReadAllText(examples).Contains("\"examples\"")
                && File.ReadAllText(recording) == "课堂录制";

            log.Add(check,
                categoriesOk && manifestShape && noOverwrite && noRestoreTemp && failureCleanup && sourceReadOnly,
                ("planFiles", plan.Files.Count), ("zipEntries", entryNames.Count),
                ("categories", categoriesOk), ("manifestShape", manifestShape),
                ("firstRestored", first.Files.Count), ("secondRestored", second.Files.Count),
                ("noOverwrite", noOverwrite), ("noRestoreTemp", noRestoreTemp),
                ("failureCleanup", failureCleanup), ("sourceReadOnly", sourceReadOnly),
                ("zip", "compat-bundle.zip"));
        }
        catch (Exception ex)
        {
            log.Add(check, false, ("error", ex.Message));
        }
    }

    // ---- 6. 无 PPT 的讲义导出 ----------------------------------------------

    private static async Task CheckHandoutExportAsync(CheckLog log, string directory)
    {
        const string check = "handout export without PPT emits one PDF page per board page and keeps source intact";
        try
        {
            var board = new BoardService();
            board.Current.Document.Add(SyntheticStroke(30, 40));
            board.AddImagesAtomic(new[] { TestImage("讲义图片", 80, 120) }, "讲义图片");
            board.AddAiObjectsAtomic(
                new[] { TestText("讲义文本", 200, 200) },
                new[] { TestFormula("x^2+y^2", 200, 340) },
                "讲义对象", "handout-group");

            string boardPath = Path.Combine(directory, "compat-handout-board.icink");
            await board.SaveBoardAsync(boardPath);
            string hashBefore = Sha256Of(boardPath);
            int strokesBefore = board.Current.Document.Count;
            int imagesBefore = board.CurrentImages.Count;

            string outputDirectory = Path.Combine(directory, "handout-out");
            var request = new HandoutRenderRequest
            {
                // 没有 PPT：空索引，只保留板书页。
                Index = new SlideContentIndex(),
                Options = new HandoutOptions { Content = HandoutContentMode.AnnotationsOnly },
                Board = board,
                PageWidthPx = 1240,
                PageHeightPx = 1754,
            };

            HandoutExportResult result = await HandoutExportService.ExportAsync(request, outputDirectory);
            int pdfPages = -1;
            long pdfLength = 0;
            if (!string.IsNullOrWhiteSpace(result.PdfPath) && File.Exists(result.PdfPath))
            {
                byte[] pdf = File.ReadAllBytes(result.PdfPath);
                pdfLength = pdf.LongLength;
                pdfPages = CountPdfPages(pdf);
            }

            bool pngOk = result.Pages.Count == 1
                && result.Pages.All(page => File.Exists(page.PageImagePath)
                    && new FileInfo(page.PageImagePath).Length > 0);
            bool sourceIntact = Sha256Of(boardPath) == hashBefore
                && board.Current.Document.Count == strokesBefore
                && board.CurrentImages.Count == imagesBefore;

            log.Add(check,
                result.Success && pdfPages == 1 && pngOk && sourceIntact,
                ("success", result.Success), ("boardPageFailed", result.BoardPageFailed),
                ("pages", result.Pages.Count), ("pdfPages", pdfPages), ("pdfBytes", pdfLength),
                ("pngNonEmpty", pngOk), ("sourceIntact", sourceIntact),
                ("warnings", string.Join(" | ", result.Warnings.Take(5))),
                ("pdf", result.PdfPath is null ? "" : Path.GetFileName(result.PdfPath)));
        }
        catch (Exception ex)
        {
            log.Add(check, false, ("error", ex.Message));
        }
    }

    private static int CountPdfPages(byte[] pdf)
    {
        string text = Encoding.ASCII.GetString(pdf);
        Match match = Regex.Match(text, @"/Type /Pages /Kids \[[^\]]*\] /Count (\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out int count) ? count : -1;
    }

    // ---- fixture 工具 -------------------------------------------------------

    private static InkStroke SyntheticStroke(float x, float y)
    {
        var stroke = new InkStroke(new StrokeAttributes
        {
            Color = InkColor.Black,
            BaseWidth = 2f,
            TaperKind = TaperKind.None,
        });
        stroke.AddPoint(x, y);
        stroke.AddPoint(x + 40, y + 20);
        stroke.AddPoint(x + 80, y + 24);
        stroke.Complete();
        return stroke;
    }

    private static BoardImageAsset TestImage(string name, float left, float top) => new()
    {
        Name = name,
        Data = Convert.FromBase64String(OnePixelPngBase64),
        ContentType = "image/png",
        Left = left,
        Top = top,
        Width = 120,
        Height = 60,
    };

    private static BoardTextObject TestText(string content, float left, float top) => new()
    {
        Type = "text",
        Content = content,
        Title = "兼容文本",
        Left = left,
        Top = top,
        Width = 220,
        Height = 80,
        Source = "ai",
        Instruction = "compatcheck",
        CreatedAt = FixedCreatedAt,
    };

    private static BoardFormulaObject TestFormula(string latex, float left, float top) => new()
    {
        Latex = latex,
        Left = left,
        Top = top,
        Width = 200,
        Height = 70,
        Source = "ai",
        Instruction = "compatcheck",
        CreatedAt = FixedCreatedAt,
    };

    private static string WriteFixture(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Sha256Of(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string StrokeSignature(InkStroke stroke)
    {
        IReadOnlyList<InkPoint> points = stroke.Points;
        if (points.Count == 0) return "empty";
        return points.Count + ":" + points[0].X.ToString("R", CultureInfo.InvariantCulture)
            + "," + points[0].Y.ToString("R", CultureInfo.InvariantCulture)
            + ":" + points[^1].X.ToString("R", CultureInfo.InvariantCulture)
            + "," + points[^1].Y.ToString("R", CultureInfo.InvariantCulture);
    }

    private static int ReadBoardVersion(string path)
    {
        using ZipArchive archive = ZipFile.OpenRead(path);
        ZipArchiveEntry? entry = archive.GetEntry("board.json");
        if (entry is null) return -1;
        using var reader = new StreamReader(entry.Open());
        JsonNode? node = JsonNode.Parse(reader.ReadToEnd());
        return node?["Version"]?.GetValue<int>() ?? -1;
    }

    private static void CleanupTemporaryDirectories(string directory)
    {
        foreach (string name in new[] { "v3-work" })
        {
            try
            {
                string path = Path.Combine(directory, name);
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch
            {
                // 清理失败不影响报告；fixture 与产物仍可供实机复核。
            }
        }
    }

    private static void WriteReport(string textPath, string jsonPath, CheckLog log, JsonSerializerOptions options)
    {
        var builder = new StringBuilder();
        foreach (CheckItem item in log.Items)
        {
            builder.Append(item.Passed ? "[PASS] " : "[FAIL] ");
            if (item.Optional) builder.Append("(optional) ");
            builder.Append(item.Check)
                .Append(" (").Append(item.ElapsedMs.ToString("0.0", CultureInfo.InvariantCulture)).Append(" ms)");
            foreach (var pair in item.Details)
                builder.Append(' ').Append(pair.Key).Append('=').Append(FormatValue(pair.Value));
            builder.AppendLine();
        }

        int passed = log.Items.Count(item => item.Passed);
        int failed = log.Items.Count(item => !item.Passed && !item.Optional);
        int optional = log.Items.Count(item => item.Optional);
        double totalElapsed = log.Items.Sum(item => item.ElapsedMs);
        builder.AppendLine();
        builder.AppendLine($"summary: {passed}/{log.Items.Count} passed, {failed} failed, {optional} optional, "
            + $"total={totalElapsed.ToString("0.0", CultureInfo.InvariantCulture)} ms");
        builder.AppendLine("note: offline fixtures only; no AI HTTP request and no real PPT is touched.");
        File.WriteAllText(textPath, builder.ToString());

        var document = new
        {
            generatedAt = DateTimeOffset.Now,
            passed = log.Passed,
            totalElapsedMs = Math.Round(totalElapsed, 1),
            networkUsed = false,
            checks = log.Items,
        };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(document, options));
    }

    private static string FormatValue(object value) => value switch
    {
        null => "null",
        string text => text.ReplaceLineEndings(" "),
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => JsonSerializer.Serialize(value),
    };
}