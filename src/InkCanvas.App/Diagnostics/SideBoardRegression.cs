using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Core.Diagnostics;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Board;
using InkCanvas.Ink;
using InkCanvas.Platform.Windows;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace InkCanvas.App.Diagnostics;

/// <summary>
/// 板中板（SideBoard）运行时诊断：在真实 App、真实 Shell 与真实 BoardService 中运行，
/// 输出可审计的文本/JSON 证据，覆盖内容保留、主/子板隔离、撤销栈、贴回主展板、
/// 保存重开、旧文件兼容与 50 次拉出/收回的内存采样。
/// </summary>
internal static class SideBoardRegression
{
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
            FileLog.WriteLogToFile($"[SideBoardCheck] {(passed ? "PASS" : "FAIL")} {check}", passed ? LogType.Info : LogType.Error);
        }

        public IReadOnlyList<CheckItem> Items => _items;

        public bool Passed => _items.All(item => item.Passed || item.Optional);
    }

    public static async Task Run(string directory, int cycles = 50)
    {
        Directory.CreateDirectory(directory);
        var log = new CheckLog();
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        string textPath = Path.Combine(directory, "sideboard-check.txt");
        string jsonPath = Path.Combine(directory, "sideboard-check.json");
        FileLog.WriteLogToFile($"[SideBoardCheck] 开始，输出目录：{directory}", LogType.Info);

        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        services.Settings.Automation.AutoSavedStrokesLocation = Path.Combine(directory, "captures");
        var main = services.Board;
        var side = services.SideBoard;
        int mainStrokesAtStart = main.Current.Document.Count;
        int mainUndoAtStart = main.Current.History.UndoCount;
        bool mainCanUndoAtStart = main.Current.History.CanUndo;
        log.Add("main board starts empty", mainStrokesAtStart == 0 && !mainCanUndoAtStart,
            ("strokes", mainStrokesAtStart), ("canUndo", mainCanUndoAtStart), ("undoCount", mainUndoAtStart));

        using var shell = new ShellController(services);
        shell.Start();
        shell.StartInClickThrough();
        SideBoardWindow sideWindow = null;
        try
        {
            shell.ToggleBoard();
            await Task.Delay(400);
            var boardWindow = shell.OpenWindows.OfType<BoardWindow>().SingleOrDefault();
            log.Add("main board window opens", boardWindow is not null,
                ("background", main.Background.ToString()), ("pageCount", main.PageCount));
            if (boardWindow is null) throw new InvalidOperationException("主展板窗口未打开。");

            shell.ToggleSideBoard();
            await Task.Delay(400);
            sideWindow = shell.CurrentSideBoard;
            bool opened = sideWindow is not null && sideWindow.IsShown
                && ReferenceEquals(sideWindow.CurrentController, side.Current);
            log.Add("side board opens through the shell toggle", opened,
                ("isShown", sideWindow?.IsShown ?? false), ("pageCount", side.PageCount));
            if (!opened) throw new InvalidOperationException("子板窗口未创建或未显示。");

            // 1) 内容保留：子板写 2 笔 → HideBoard → ShowBoard → 笔画数仍为 2。
            try
            {
                var controller = sideWindow.CurrentController;
                PrepareController(controller);
                DrawStroke(controller, 9101, 120, 120, 260, 40);
                DrawStroke(controller, 9102, 160, 230, 240, -20);
                int before = side.Current.Document.Count;
                sideWindow.HideBoard(animate: false);
                await Task.Delay(40);
                bool hidden = !sideWindow.IsShown;
                sideWindow.ShowBoard(animate: false);
                await Task.Delay(80);
                int after = side.Current.Document.Count;
                log.Add("sub-board ink survives hide and show", before == 2 && after == 2 && hidden && sideWindow.IsShown,
                    ("strokesBefore", before), ("strokesAfter", after), ("hiddenWhileClosed", hidden),
                    ("isShown", sideWindow.IsShown));
                try
                {
                    bool captured = sideWindow.Content is FrameworkElement root
                        && await BoardWindowRegression.SaveVisual(root, Path.Combine(directory, "sideboard-show.png"));
                    log.AddOptional("sub-board visual captured", captured, ("file", "sideboard-show.png"));
                }
                catch (Exception ex)
                {
                    log.AddOptional("sub-board visual captured", false, ("error", ex.Message));
                }
            }
            catch (Exception ex) { log.Add("content retention exception", false, ("error", ex.ToString())); }

            // 2) 隔离：主板 0 笔且不可撤销；子板写笔/撤销后主板完全不变。
            try
            {
                int mainStrokesBefore = main.Current.Document.Count;
                bool mainCanUndoBefore = main.Current.History.CanUndo;
                int mainUndoCountBefore = main.Current.History.UndoCount;
                int sideStrokesBefore = side.Current.Document.Count;
                var controller = sideWindow.CurrentController;
                PrepareController(controller);
                DrawStroke(controller, 9103, 220, 140, 200, 60);
                int sideAfterDraw = side.Current.Document.Count;
                controller.Undo();
                int sideAfterUndo = side.Current.Document.Count;
                int mainStrokesAfter = main.Current.Document.Count;
                bool mainCanUndoAfter = main.Current.History.CanUndo;
                int mainUndoCountAfter = main.Current.History.UndoCount;
                bool isolated = mainStrokesBefore == 0 && !mainCanUndoBefore
                    && sideAfterDraw == sideStrokesBefore + 1 && sideAfterUndo == sideStrokesBefore
                    && mainStrokesAfter == mainStrokesBefore && mainCanUndoAfter == mainCanUndoBefore
                    && mainUndoCountAfter == mainUndoCountBefore;
                log.Add("sub-board ink and undo leave the main board untouched", isolated,
                    ("mainStrokes", mainStrokesAfter), ("mainCanUndo", mainCanUndoAfter),
                    ("mainUndoCount", mainUndoCountAfter), ("sideStrokes", sideAfterUndo));
            }
            catch (Exception ex) { log.Add("isolation exception", false, ("error", ex.ToString())); }

            // 3) 撤销栈：子板新页写 2 笔 → Undo=1 → Redo=2；主板 History 计数不变。
            try
            {
                int mainUndoBefore = main.Current.History.UndoCount;
                bool inserted = side.InsertPageAfterCurrent();
                await Task.Delay(40);
                var controller = sideWindow.CurrentController;
                PrepareController(controller);
                DrawStroke(controller, 9201, 140, 140, 240, 30);
                DrawStroke(controller, 9202, 180, 230, 220, -20);
                int afterTwo = controller.Document.Count;
                controller.Undo();
                int afterUndo = controller.Document.Count;
                bool canRedo = controller.History.CanRedo;
                controller.Redo();
                int afterRedo = controller.Document.Count;
                int mainUndoAfter = main.Current.History.UndoCount;
                bool passed = inserted && side.PageCount == 2 && side.CurrentIndex == 1
                    && ReferenceEquals(controller, side.Current)
                    && afterTwo == 2 && afterUndo == 1 && canRedo && afterRedo == 2
                    && mainUndoAfter == mainUndoBefore;
                log.Add("sub-board undo and redo keep the main history untouched", passed,
                    ("sidePage", side.CurrentIndex), ("sidePages", side.PageCount),
                    ("afterTwo", afterTwo), ("afterUndo", afterUndo), ("afterRedo", afterRedo),
                    ("mainUndoBefore", mainUndoBefore), ("mainUndoAfter", mainUndoAfter));
            }
            catch (Exception ex) { log.Add("undo stack exception", false, ("error", ex.ToString())); }

            // 4) 贴到主展板：插入 1 张图片，撤销后回到原数量；并按 BoardPasteLayout 的语义
            //    读取贴回后的真实资产几何，断言居中、宽度上限（视口 60%）与源宽高比。
            try
            {
                int imagesBefore = main.CurrentImages.Count;
                var imageIdsBefore = main.CurrentImages.Select(image => image.Id).ToHashSet();
                InkCanvas.Core.Ink.Rect sourceBounds = PageContentBounds(side.Current);
                bool hasSource = !sourceBounds.IsEmpty && sourceBounds.Width > 0 && sourceBounds.Height > 0;
                var viewport = boardWindow.PasteViewport;
                double widthLimit = viewport.X * BoardPasteLayout.DefaultMaxVisibleWidthFraction;

                bool pasted = await sideWindow.PasteCurrentPageToAsync(boardWindow);
                int imagesAfter = main.CurrentImages.Count;
                var asset = main.CurrentImages.FirstOrDefault(image => !imageIdsBefore.Contains(image.Id));

                double centerDx = -1, centerDy = -1;
                double assetWidth = -1, assetHeight = -1, assetLeft = -1, assetTop = -1;
                double sourceAspect = -1, pastedAspect = -1, aspectError = -1;
                if (asset is not null)
                {
                    assetLeft = asset.Left;
                    assetTop = asset.Top;
                    assetWidth = asset.Width;
                    assetHeight = asset.Height;
                    centerDx = Math.Abs(asset.Left + asset.Width / 2.0 - viewport.X / 2.0);
                    centerDy = Math.Abs(asset.Top + asset.Height / 2.0 - viewport.Y / 2.0);
                    if (hasSource)
                    {
                        sourceAspect = sourceBounds.Width / (double)sourceBounds.Height;
                        pastedAspect = asset.Width / (double)asset.Height;
                        aspectError = Math.Abs(pastedAspect / sourceAspect - 1.0);
                    }
                }

                main.Current.Undo();
                main.Current.ClearSelection();
                int imagesAfterUndo = main.CurrentImages.Count;
                log.Add("paste to main board inserts one image and undo removes it",
                    pasted && imagesBefore == 0 && imagesAfter == imagesBefore + 1 && imagesAfterUndo == imagesBefore,
                    ("pasted", pasted), ("imagesBefore", imagesBefore), ("imagesAfter", imagesAfter),
                    ("imagesAfterUndo", imagesAfterUndo),
                    ("assetLeft", assetLeft), ("assetTop", assetTop), ("assetWidth", assetWidth), ("assetHeight", assetHeight));

                log.Add("pasted image is centered in the main viewport",
                    asset is not null && centerDx >= 0 && centerDy >= 0 && centerDx <= 2 && centerDy <= 2,
                    ("viewportWidth", viewport.X), ("viewportHeight", viewport.Y),
                    ("assetCenterX", asset is null ? -1 : asset.Left + asset.Width / 2.0),
                    ("assetCenterY", asset is null ? -1 : asset.Top + asset.Height / 2.0),
                    ("viewportCenterX", viewport.X / 2.0), ("viewportCenterY", viewport.Y / 2.0),
                    ("centerDeviationX", centerDx), ("centerDeviationY", centerDy), ("limit", 2));

                log.Add("pasted image width stays within 60 percent of the main viewport",
                    asset is not null && assetWidth > 0 && assetWidth <= widthLimit + 2,
                    ("assetWidth", assetWidth), ("viewportWidth", viewport.X),
                    ("widthLimit", widthLimit), ("tolerance", 2),
                    ("widthFraction", asset is null || viewport.X <= 0 ? -1 : assetWidth / viewport.X));

                log.Add("pasted image keeps the source aspect ratio within 1 percent",
                    asset is not null && aspectError >= 0 && aspectError < 0.01,
                    ("sourceBounds", $"{sourceBounds.Left},{sourceBounds.Top} {sourceBounds.Width}x{sourceBounds.Height}"),
                    ("sourceAspect", sourceAspect), ("pastedAspect", pastedAspect),
                    ("relativeError", aspectError), ("limit", 0.01));
            }
            catch (Exception ex) { log.Add("paste to main board exception", false, ("error", ex.ToString())); }

            // 4b) 视口/选区隔离：子板缩放/平移并选中笔画，主板的视口与选区必须完全不变。
            try
            {
                var mainController = main.Current;
                var sideController = sideWindow.CurrentController;
                bool distinctControllers = !ReferenceEquals(mainController, sideController);
                var mainViewBefore = mainController.ViewTransform;
                var sideViewBefore = sideController.ViewTransform;

                var shiftedView = Matrix3x2.CreateScale(1.35f) * Matrix3x2.CreateTranslation(36f, -24f);
                sideController.SetView(shiftedView);
                await Task.Delay(60);
                bool sideViewChanged = sideController.ViewTransform.Equals(shiftedView);
                bool mainViewKept = mainController.ViewTransform.Equals(mainViewBefore);

                var probe = sideController.Document.Strokes.FirstOrDefault();
                string probeId = probe?.Id.ToString() ?? "";
                if (probe is not null) sideController.Selection.Add(probe);
                bool sideHasSelection = probe is not null && sideController.HasSelection;
                bool mainSelectionEmpty = !mainController.HasSelection && mainController.Selection.Count == 0;

                sideController.ClearSelection();
                sideController.SetView(sideViewBefore);
                await Task.Delay(40);
                bool restored = sideController.ViewTransform.Equals(sideViewBefore)
                    && !sideController.HasSelection
                    && mainController.ViewTransform.Equals(mainViewBefore)
                    && !mainController.HasSelection;

                log.Add("sub-board view and selection stay isolated from the main board",
                    distinctControllers && sideViewChanged && mainViewKept
                        && sideHasSelection && mainSelectionEmpty && restored,
                    ("distinctControllers", distinctControllers),
                    ("sideViewBefore", DescribeMatrix(sideViewBefore)),
                    ("sideViewApplied", DescribeMatrix(shiftedView)),
                    ("sideViewAfter", DescribeMatrix(sideController.ViewTransform)),
                    ("sideViewChanged", sideViewChanged),
                    ("mainViewBefore", DescribeMatrix(mainViewBefore)),
                    ("mainViewAfter", DescribeMatrix(mainController.ViewTransform)),
                    ("mainViewKept", mainViewKept),
                    ("probeStrokeId", probeId), ("sideHasSelection", sideHasSelection),
                    ("mainSelectionCount", mainController.Selection.Count),
                    ("mainSelectionEmpty", mainSelectionEmpty), ("restored", restored));
            }
            catch (Exception ex) { log.Add("sub-board view and selection isolation exception", false, ("error", ex.ToString())); }

            // 5) 保存/重开：主 BoardService 写 side.icink，新实例加载后页数/笔画/页索引一致。
            string savePath = Path.Combine(directory, "side.icink");
            try
            {
                int mainPages = main.PageCount;
                int sidePages = side.PageCount;
                int sideIndex = side.CurrentIndex;
                int sideStrokes = side.Current.Document.Count;
                await main.SaveBoardAsync(savePath);
                var reloaded = new BoardService();
                var reloadedSide = new BoardService();
                reloaded.SideBoardSource = reloadedSide;
                try
                {
                    await reloaded.LoadBoardAsync(savePath);
                    bool matches = reloaded.PageCount == mainPages && reloadedSide.PageCount == sidePages
                        && reloadedSide.CurrentIndex == sideIndex
                        && reloadedSide.Current.Document.Count == sideStrokes && sideStrokes == 2;
                    log.Add("save and reopen restores side pages, strokes and page index", matches,
                        ("file", Path.GetFileName(savePath)), ("mainPages", reloaded.PageCount),
                        ("sidePages", reloadedSide.PageCount), ("sideIndex", reloadedSide.CurrentIndex),
                        ("sideStrokes", reloadedSide.Current.Document.Count),
                        ("bytes", new FileInfo(savePath).Length));
                }
                finally
                {
                    foreach (var page in reloaded.Pages) page.Dispose();
                    foreach (var page in reloadedSide.Pages) page.Dispose();
                }
            }
            catch (Exception ex) { log.Add("save and reopen exception", false, ("error", ex.ToString())); }

            // 6) 旧文件兼容：把 side.icink 降为无 SideBoard 节点的 v3 包，加载后子板为空。
            string v3Path = Path.Combine(directory, "side-v3.icink");
            try
            {
                DowngradeToV3(savePath, v3Path);
                var legacy = new BoardService();
                var legacySide = new BoardService();
                legacy.SideBoardSource = legacySide;
                try
                {
                    await legacy.LoadBoardAsync(v3Path);
                    bool compatible = legacy.PageCount == 1
                        && legacy.Current.Document.Count == main.Current.Document.Count
                        && legacySide.PageCount == 1 && legacySide.Current.Document.Count == 0;
                    log.Add("v3 package without a side node loads with an empty sub-board", compatible,
                        ("file", Path.GetFileName(v3Path)), ("version", 3),
                        ("mainPages", legacy.PageCount), ("sidePages", legacySide.PageCount),
                        ("sideStrokes", legacySide.Current.Document.Count));
                }
                finally
                {
                    foreach (var page in legacy.Pages) page.Dispose();
                    foreach (var page in legacySide.Pages) page.Dispose();
                }
            }
            catch (Exception ex) { log.Add("legacy v3 compatibility exception", false, ("error", ex.ToString())); }

            // 7) N 次动画拉出/收回：每轮 ShowBoard()/HideBoard() 走真实动画并等待完成，
            //    断言笔画数不变与窗口真正显示/隐藏，记录每轮耗时并采样句柄/内存。
            //    默认 50 轮；--sideboardcheck-long 用 500 轮做长稳观察。
            try
            {
                int stable = side.Current.Document.Count;
                // 归位：上一项结束时子板仍可见，先无动画收回，保证第 1 轮从隐藏态开始。
                sideWindow.HideBoard(animate: false);
                await Task.Delay(60);
                var process = Process.GetCurrentProcess();
                process.Refresh();
                long startHandles = process.HandleCount;
                long startWorkingSet = process.WorkingSet64;
                long startPrivate = process.PrivateMemorySize64;
                var samples = new List<Dictionary<string, object>>();
                var rounds = new List<Dictionary<string, object>>();
                var animationFailures = new List<Dictionary<string, object>>();
                int failures = 0;
                int firstFailure = -1;
                var totalWatch = Stopwatch.StartNew();
                for (int round = 1; round <= cycles; round++)
                {
                    var watch = Stopwatch.StartNew();
                    sideWindow.ShowBoard(animate: true);
                    bool shown = await WaitForAsync(() =>
                        !sideWindow.IsAnimating && sideWindow.IsShown && sideWindow.AppWindow.IsVisible);
                    long showMs = watch.ElapsedMilliseconds;
                    bool strokesAfterShow = side.Current.Document.Count == stable;

                    watch.Restart();
                    sideWindow.HideBoard(animate: true);
                    bool hidden = await WaitForAsync(() =>
                        !sideWindow.IsAnimating && !sideWindow.IsShown && !sideWindow.AppWindow.IsVisible);
                    long hideMs = watch.ElapsedMilliseconds;
                    bool strokesAfterHide = side.Current.Document.Count == stable;

                    if (!(shown && hidden && strokesAfterShow && strokesAfterHide))
                    {
                        failures++;
                        if (firstFailure < 0) firstFailure = round;
                        if (animationFailures.Count < 10)
                            animationFailures.Add(new Dictionary<string, object>
                            {
                                ["round"] = round,
                                ["shown"] = shown,
                                ["hidden"] = hidden,
                                ["isShown"] = sideWindow.IsShown,
                                ["isVisible"] = sideWindow.AppWindow.IsVisible,
                                ["isAnimating"] = sideWindow.IsAnimating,
                                ["strokes"] = side.Current.Document.Count,
                            });
                    }
                    rounds.Add(new Dictionary<string, object>
                    {
                        ["round"] = round,
                        ["showMs"] = showMs,
                        ["hideMs"] = hideMs,
                        ["roundMs"] = showMs + hideMs,
                        ["strokes"] = side.Current.Document.Count,
                        ["shown"] = shown,
                        ["hidden"] = hidden,
                    });
                    if (round % 10 == 0)
                    {
                        process.Refresh();
                        samples.Add(new Dictionary<string, object>
                        {
                            ["iteration"] = round,
                            ["handleCount"] = process.HandleCount,
                            ["workingSetBytes"] = process.WorkingSet64,
                            ["privateBytes"] = process.PrivateMemorySize64,
                        });
                    }
                }
                totalWatch.Stop();
                process.Refresh();
                long endHandles = process.HandleCount;
                long endWorkingSet = process.WorkingSet64;
                long endPrivate = process.PrivateMemorySize64;
                bool passed = failures == 0 && stable == 2 && side.Current.Document.Count == stable;
                log.Add($"{cycles} animated show/hide cycles keep sub-board strokes and visibility", passed,
                    ("animation", "animate:true, 210ms slide, poll 15ms, timeout 2000ms"),
                    ("strokes", stable), ("failures", failures), ("firstFailure", firstFailure),
                    ("roundsMeasured", rounds.Count), ("totalElapsedMs", totalWatch.ElapsedMilliseconds),
                    ("roundsTotalMs", rounds.Sum(item => Convert.ToInt64(item["roundMs"]))),
                    ("slowestRoundMs", rounds.Max(item => Convert.ToInt64(item["roundMs"]))),
                    ("handlesStart", startHandles), ("handlesEnd", endHandles), ("handlesGrowth", endHandles - startHandles),
                    ("workingSetStartBytes", startWorkingSet), ("workingSetEndBytes", endWorkingSet),
                    ("workingSetGrowthBytes", endWorkingSet - startWorkingSet),
                    ("privateStartBytes", startPrivate), ("privateEndBytes", endPrivate),
                    ("privateGrowthBytes", endPrivate - startPrivate),
                    ("samples", samples), ("rounds", rounds), ("animationFailures", animationFailures));

                // 句柄增长阈值：每轮 Show/Hide 都新建 Win32/XAML 资源，若未回收会线性增长。
                // 实测 50 轮约 +93；500 轮长稳同样沿用该阈值。
                const long handleGrowthLimit = 300;
                long handleGrowth = endHandles - startHandles;
                log.Add($"{cycles} animated cycles keep handle growth within the limit",
                    handleGrowth <= handleGrowthLimit,
                    ("handlesStart", startHandles), ("handlesEnd", endHandles),
                    ("handlesGrowth", handleGrowth), ("limit", handleGrowthLimit));
            }
            catch (Exception ex) { log.Add("animated show/hide cycle exception", false, ("error", ex.ToString())); }

            // 8) 边界夹取与最小尺寸：屏幕外/右下越界后 EnsureOnScreen 必须回到工作区且不小于最小尺寸。
            try
            {
                sideWindow.ShowBoard(animate: false);
                await Task.Delay(80);
                await WaitForAsync(() => !sideWindow.IsAnimating);
                var workArea = WindowChrome.GetWorkArea(boardWindow);
                double dpi = WindowChrome.GetDpiScale(new RectInt32(workArea.X, workArea.Y, workArea.Width, workArea.Height));
                int minimumWidth = Math.Min((int)Math.Ceiling(SideBoardLayout.MinimumWidthDip * dpi), workArea.Width);
                int minimumHeight = Math.Min((int)Math.Ceiling(SideBoardLayout.MinimumHeightDip * dpi), workArea.Height);
                string workText = $"{workArea.X},{workArea.Y} {workArea.Width}x{workArea.Height}";

                async Task<(SideBoardRect Before, SideBoardRect After)> ClampFrom(int x, int y, int width, int height)
                {
                    WindowChrome.Place(sideWindow, x, y, width, height);
                    await Task.Delay(40);
                    var before = WindowRect(sideWindow);
                    sideWindow.EnsureOnScreen();
                    await Task.Delay(120);
                    return (before, WindowRect(sideWindow));
                }

                bool Inside(SideBoardRect rect) => rect.X >= workArea.X - 1 && rect.Y >= workArea.Y - 1
                    && rect.Right <= workArea.X + workArea.Width + 1
                    && rect.Bottom <= workArea.Y + workArea.Height + 1;
                bool Minimum(SideBoardRect rect) => rect.Width >= minimumWidth - 1 && rect.Height >= minimumHeight - 1;
                (string Key, object Value)[] Geometry(SideBoardRect before, SideBoardRect after, string requested) => new (string, object)[]
                {
                    ("requested", requested), ("before", DescribeRect(before)), ("after", DescribeRect(after)),
                    ("workArea", workText), ("dpi", dpi),
                    ("minimumWidthPx", minimumWidth), ("minimumHeightPx", minimumHeight),
                };

                var offscreen = await ClampFrom(-5000, -5000, 120, 90);
                log.Add("offscreen placement is clamped into the work area", Inside(offscreen.After),
                    Geometry(offscreen.Before, offscreen.After, "-5000,-5000 120x90"));
                log.Add("offscreen placement keeps the minimum window size", Minimum(offscreen.After),
                    Geometry(offscreen.Before, offscreen.After, "-5000,-5000 120x90"));

                var corner = await ClampFrom(workArea.X + workArea.Width - 50, workArea.Y + workArea.Height - 40, 120, 90);
                log.Add("bottom-right overflow is clamped into the work area", Inside(corner.After),
                    Geometry(corner.Before, corner.After, $"{workArea.X + workArea.Width - 50},{workArea.Y + workArea.Height - 40} 120x90"));
                log.Add("bottom-right overflow keeps the minimum window size", Minimum(corner.After),
                    Geometry(corner.Before, corner.After, $"{workArea.X + workArea.Width - 50},{workArea.Y + workArea.Height - 40} 120x90"));

                // 应用内缩放：项目存在 Appearance.FloatingBarScale（百分比）。它不参与 SideBoard 布局计算
                // （布局只用系统 DPI），这里在 150% 下重复一次并记录设置实际值。
                services.Settings.Appearance.FloatingBarScale = 150;
                services.ApplySettings();
                try
                {
                    await Task.Delay(80);
                    var scaled = await ClampFrom(-5000, -5000, 120, 90);
                    log.Add("offscreen placement is clamped at 150% app scale", Inside(scaled.After),
                        Geometry(scaled.Before, scaled.After, "-5000,-5000 120x90").Append(("scaleSetting", (object)"FloatingBarScale"))
                            .Append(("scalePercent", (object)services.Settings.Appearance.FloatingBarScale))
                            .Append(("affectsSideBoardLayout", (object)false)).ToArray());
                    log.Add("offscreen placement keeps the minimum size at 150% app scale", Minimum(scaled.After),
                        Geometry(scaled.Before, scaled.After, "-5000,-5000 120x90").Append(("scaleSetting", (object)"FloatingBarScale"))
                            .Append(("scalePercent", (object)services.Settings.Appearance.FloatingBarScale))
                            .Append(("affectsSideBoardLayout", (object)false)).ToArray());
                }
                finally
                {
                    services.Settings.Appearance.FloatingBarScale = 100;
                    services.ApplySettings();
                }
            }
            catch (Exception ex) { log.Add("bounds clamp and minimum size exception", false, ("error", ex.ToString())); }
        }
        catch (Exception ex) { log.Add("side board regression exception", false, ("error", ex.ToString())); }
        finally
        {
            try { sideWindow?.HideBoard(animate: false); } catch { }
            foreach (var window in shell.OpenWindows.OfType<BoardWindow>().ToArray())
            {
                try { window.Close(); } catch { }
            }
            try { shell.Dispose(); } catch { }
            WriteReport(textPath, jsonPath, log, jsonOptions);
            bool passed = log.Passed;
            FileLog.WriteLogToFile(
                $"[SideBoardCheck] 完成：{log.Items.Count(item => item.Passed)}/{log.Items.Count} 通过，退出码 {(passed ? 0 : 1)}",
                passed ? LogType.Info : LogType.Error);
            Environment.ExitCode = passed ? 0 : 1;
            Application.Current.Exit();
        }
    }

    /// <summary>轮询等待 UI 线程上的动画/可见性条件，超时返回 false；不阻塞消息循环。</summary>
    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds >= timeoutMs) return false;
            await Task.Delay(15);
        }
        return true;
    }

    private static SideBoardRect WindowRect(SideBoardWindow window)
    {
        var position = window.AppWindow.Position;
        var size = window.AppWindow.Size;
        return new SideBoardRect(position.X, position.Y, size.Width, size.Height);
    }

    private static string DescribeRect(SideBoardRect rect) => $"{rect.X},{rect.Y} {rect.Width}x{rect.Height}";

    private static string DescribeMatrix(Matrix3x2 matrix)
        => string.Create(CultureInfo.InvariantCulture,
            $"{matrix.M11:0.###},{matrix.M12:0.###},{matrix.M21:0.###},{matrix.M22:0.###},{matrix.M31:0.###},{matrix.M32:0.###}");

    /// <summary>与 SideBoardWindow.CurrentPageContentBounds 同语义：贴回内容的世界坐标包围盒。</summary>
    private static InkCanvas.Core.Ink.Rect PageContentBounds(InkController controller)
    {
        InkCanvas.Core.Ink.Rect bounds = InkCanvas.Core.Ink.Rect.Empty;
        foreach (var item in controller.DrawingObjects)
        {
            if (item.Stroke is { } stroke) bounds = bounds.Union(stroke.Bounds);
            else if (item.Element is IImageSelectionElement { ImageData.Length: > 0 } image)
                bounds = bounds.Union(image.Bounds);
            else if (item.Element is IBoardTextElement or IBoardFormulaElement)
                bounds = bounds.Union(item.Element.Bounds);
        }
        return bounds;
    }

    private static void PrepareController(InkController controller)
    {
        controller.Tool = InkTool.Pen;
        controller.RecognizeShapes = false;
        controller.PalmErases = false;
        controller.MultiTouchEnabled = false;
    }

    /// <summary>合成一次真实笔按下/移动/抬起，走 InkController 的提交与撤销栈路径。</summary>
    private static void DrawStroke(InkController controller, uint id, float x, float y, float dx, float dy)
    {
        var down = new OverlayPointer(id, x, y, 0.5f, 1f, 1f, OverlayPointerKind.Pen, 100);
        controller.OnPointerDown(down);
        controller.OnPointerMove(down with { X = x + dx * 0.5f, Y = y + dy * 0.5f, TimeMs = 120 });
        controller.OnPointerUp(down with { X = x + dx, Y = y + dy, TimeMs = 140 });
    }

    /// <summary>把 v4 包重打包为 v3：删除 SideBoard 节点、移除 side 作用域媒体引用。</summary>
    private static void DowngradeToV3(string source, string destination)
    {
        using var input = ZipFile.OpenRead(source);
        if (File.Exists(destination)) File.Delete(destination);
        using var output = ZipFile.Open(destination, ZipArchiveMode.Create);
        foreach (var entry in input.Entries)
        {
            bool manifest = entry.FullName is "board.json" or "board.pretty.json";
            var created = output.CreateEntry(entry.FullName, manifest ? CompressionLevel.Fastest : CompressionLevel.NoCompression);
            using var target = created.Open();
            using var sourceStream = entry.Open();
            if (!manifest) { sourceStream.CopyTo(target); continue; }

            using var reader = new StreamReader(sourceStream, Encoding.UTF8);
            var node = JsonNode.Parse(reader.ReadToEnd()) as JsonObject
                ?? throw new InvalidDataException("画板清单不是 JSON 对象。");
            node["Version"] = 3;
            node.Remove("SideBoard");
            if (node["Blobs"] is JsonArray blobs)
            {
                var mainOnly = new JsonArray();
                foreach (var blob in blobs)
                {
                    if (blob is JsonObject item && item["Scope"] is JsonValue scopeValue
                        && scopeValue.TryGetValue<string>(out var scope)
                        && string.Equals(scope, "side", StringComparison.Ordinal)) continue;
                    mainOnly.Add(blob?.DeepClone());
                }
                node["Blobs"] = mainOnly;
            }
            using var writer = new StreamWriter(target, new UTF8Encoding(false), leaveOpen: true);
            writer.Write(node.ToJsonString(new JsonSerializerOptions { WriteIndented = entry.FullName == "board.pretty.json" }));
        }
    }

    private static void WriteReport(string textPath, string jsonPath, CheckLog log, JsonSerializerOptions options)
    {
        var builder = new StringBuilder();
        foreach (var item in log.Items)
        {
            builder.Append(item.Passed ? "[PASS] " : "[FAIL] ");
            if (item.Optional) builder.Append("(optional) ");
            builder.Append(item.Check)
                .Append(" (").Append(item.ElapsedMs.ToString("0.0", CultureInfo.InvariantCulture)).Append(" ms)");
            foreach (var pair in item.Details)
                builder.Append(' ').Append(pair.Key).Append('=')
                    .Append(pair.Key == "rounds" && pair.Value is System.Collections.ICollection collection
                        ? $"[{collection.Count} rounds]"
                        : FormatValue(pair.Value));
            builder.AppendLine();
        }
        int passed = log.Items.Count(item => item.Passed);
        int failed = log.Items.Count(item => !item.Passed && !item.Optional);
        int optional = log.Items.Count(item => item.Optional);
        double totalElapsed = log.Items.Sum(item => item.ElapsedMs);
        builder.AppendLine();
        builder.AppendLine($"summary: {passed}/{log.Items.Count} passed, {failed} failed, {optional} optional, "
            + $"total={totalElapsed.ToString("0.0", CultureInfo.InvariantCulture)} ms");
        File.WriteAllText(textPath, builder.ToString());
        var document = new
        {
            generatedAt = DateTimeOffset.Now,
            passed = log.Passed,
            totalElapsedMs = Math.Round(totalElapsed, 1),
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