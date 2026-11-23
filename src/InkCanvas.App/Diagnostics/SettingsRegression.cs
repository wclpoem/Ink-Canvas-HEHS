using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Board;
using InkCanvas.Extensibility;
using InkCanvas.Extensions;
using InkCanvas.Host.Contracts;
using InkCanvas.Ink;
using InkCanvas.Platform.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class SettingsRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, Guid.NewGuid().ToString("N")));
        var originalPenColor = services.Settings.Canvas.PenColor;
        int originalTheme = services.Settings.Appearance.Theme;
        services.Settings.Canvas.PenColor = null;
        services.Settings.Appearance.Theme = 1;
        services.ApplySettings();
        checks.Add(new { check = "default pen is red in light theme without overriding a saved color",
            passed = services.OverlayInk.CurrentAttributes.Color == InkCanvas.Core.Ink.InkColor.Red && services.Settings.Canvas.PenColor is null });
        services.Settings.Appearance.Theme = 2;
        services.ApplySettings();
        checks.Add(new { check = "default pen is light in dark theme", passed = services.OverlayInk.CurrentAttributes.Color == InkCanvas.Core.Ink.InkColor.White });
        services.Settings.Canvas.PenColor = originalPenColor;
        services.Settings.Appearance.Theme = originalTheme;
        services.ApplySettings();
        using var updater = new UpdateCoordinator(services);
        var bar = new FloatingBarWindow();
        bar.Bar.Settings = services.Settings;
        bar.Bar.Controller = services.OverlayInk;
        bar.Bar.Build();
        WindowChrome.ShowWithoutActivation(bar);
        SettingsWindow window = null;
        try
        {
            string original = JsonSerializer.Serialize(services.Settings);
            Open();
            await Task.Delay(600);
            await CheckModuleSettingsAsync(window, services, checks);
            if (Environment.GetCommandLineArgs().Contains("--module-settings-only")) return;
            if (Environment.GetCommandLineArgs().Contains("--settingslayout-only"))
            {
                await CheckScrollViewportAsync(window, directory, checks);
                return;
            }
            Check("opening settings preserves stored values", JsonSerializer.Serialize(services.Settings) == original);
            Check("settings permit keyboard activation", !NativeMethods.HasExStyle(WindowChrome.GetHwnd(window), NativeMethods.WS_EX_NOACTIVATE));
            var searchTimer = System.Diagnostics.Stopwatch.StartNew();
            var searchHits = window.SearchSettings("荧光笔");
            Check("search indexes real setting labels across categories", searchHits.Any(hit => hit.Title == "荧光笔粗细")
                && searchHits.Any(hit => hit.Title == "荧光笔透明度"));
            Check("search indexing preserves settings and selected category", JsonSerializer.Serialize(services.Settings) == original
                && Descendants(window.Content).OfType<WindowWorkspace>().Single().SelectedPage == "常规");
            Check("search index builds without blocking the window", searchTimer.ElapsedMilliseconds < 1500);
            Check("search includes choice values", window.SearchSettings("深色").Any(hit => hit.Title == "颜色主题"));
            Check("search includes ink hold and hardware brush settings", window.SearchSettings("停笔").Any()
                && window.SearchSettings("硬件压感").Any() && window.SearchSettings("方向吸附").Any());
            var highlighterMatches = window.SearchSettings("画板 荧光笔");
            Check("search handles case and category terms", window.SearchSettings("powerpoint").Any()
                && highlighterMatches.Any(hit => hit.Title == "荧光笔粗细")
                && highlighterMatches.Any(hit => hit.Title == "荧光笔透明度")
                && highlighterMatches.All(hit => hit.Category == "书写与擦除"));
            Check("search missing and empty queries have no matches", window.SearchSettings("不存在的设置条目").Count == 0
                && window.SearchSettings(" ").Count == 0);
            var widthHit = searchHits.Single(hit => hit.Title == "荧光笔粗细");
            Check("search result opens matching category", window.NavigateToSetting(widthHit)
                && Descendants(window.Content).OfType<WindowWorkspace>().Single().SelectedPage == "书写与擦除");
            ((FrameworkElement)window.Content).UpdateLayout();
            await Task.Delay(100);
            Check("search result exposes original editable setting", Find<Slider>("荧光笔粗细") is { IsEnabled: true });
            await SaveSettingsVisual(window, Path.Combine(directory, "settings-search-result.png"));
            Check("settings uses shared WinUI title bar", window.Content is WindowFrame { Header.Title: "Ink Canvas 设置" }
                && window.ExtendsContentIntoTitleBar);
            Check("update search includes release list entry", window.SearchSettings("版本列表").Any());
            window.SelectCategory("关于与更新");
            ((FrameworkElement)window.Content).UpdateLayout();
            Check("release list and update status are visible in settings", Descendants(window.Content).OfType<TextBlock>().Any(text => text.Text == "版本列表")
                && Descendants(window.Content).OfType<TextBlock>().Any(text => text.Text == updater.Status));
            var searchBox = Descendants(window.Content).OfType<AutoSuggestBox>().Single();
            searchBox.Text = "双指缩放";
            ((FrameworkElement)window.Content).UpdateLayout();
            Invoke(Descendants(searchBox).OfType<Button>().Single(button => button.Name == "QueryButton"));
            await Task.Delay(80);
            Check("search button navigates through the normal query event", Descendants(window.Content).OfType<WindowWorkspace>().Single().SelectedPage == "触控与手势"
                && Find<ToggleSwitch>("双指缩放") is not null);
            var originalSize = window.AppWindow.ClientSize;
            window.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(640, 480)); await Task.Delay(100);
            var navigation = Descendants(window.Content).OfType<NavigationView>().Single();
            var workspace = Descendants(window.Content).OfType<WindowWorkspace>().Single();
            workspace.FocusSearch(); await Task.Delay(100);
            var focused = FocusManager.GetFocusedElement(((FrameworkElement)window.Content).XamlRoot);
            Check("search focus opens narrow navigation and reaches search editor", navigation.IsPaneOpen
                && Descendants(searchBox).Any(element => ReferenceEquals(element, focused)));
            window.NavigateToSetting(widthHit);
            Check("narrow-window search closes overlay navigation to reveal setting", !navigation.IsPaneOpen);
            window.AppWindow.ResizeClient(originalSize);
            searchBox.Text = "不存在的设置条目";
            Invoke(Descendants(searchBox).OfType<Button>().Single(button => button.Name == "QueryButton"));
            Check("missing search query provides visible feedback", Descendants(window.Content).OfType<InfoBar>().Any(info => info.IsOpen && info.Message == "没有找到相关设置"));

            var groupedNavigation = Descendants(window.Content).OfType<NavigationView>().Single();
            Check("settings navigation exposes three labeled groups", groupedNavigation.MenuItems.OfType<NavigationViewItemHeader>()
                .Select(item => item.Content?.ToString()).SequenceEqual(new[] { "使用与书写", "课堂与扩展", "应用与数据" }));
            var migratedSettings = new[]
            {
                ("常规", "关闭软件时二次弹窗确认"), ("常规", "自动查杀 希沃白板5"),
                ("书写与擦除", "荧光笔粗细"), ("画布与识别", "切换到鼠标模式后自动清屏"),
                ("画布与识别", "启用墨迹识别"), ("触控与手势", "启用笔尖模式"),
                ("触控与手势", "所有显示器均可书写"), ("外观与工具栏", "进入“希沃轻白板”时自动收纳"),
                ("外观与工具栏", "在调色盘窗口中显示 启用笔尖模式 切换按钮"),
                ("演示与课件", "进入PPT 放映时自动收纳至侧边栏"),
                ("保存与备份", "清屏时自动截图"), ("保存与备份", "墨迹、截图自动保存位置"),
                ("保存与备份", "保存时间设置（Days）"), ("关于与更新", "自动检查更新"),
                ("关于与更新", "记录日志"),
            };
            foreach (var (category, title) in migratedSettings)
            {
                var matches = window.SearchSettings(title).Where(hit => hit.Title == title).ToArray();
                Check("migrated setting has one searchable location " + title, matches.Length == 1 && matches[0].Category == category);
                Check("migrated setting opens its editor " + title, matches.Length == 1 && window.NavigateToSetting(matches[0])
                    && Descendants(window.Content).OfType<WindowWorkspace>().Single().SelectedPage == category);
            }
            foreach (var page in SettingsCategoryCatalog.Pages)
            {
                window.SelectCategory(page.Name);
                ((FrameworkElement)window.Content).UpdateLayout();
                Check("grouped navigation opens a populated page " + page.Name,
                    Descendants(window.Content).OfType<WindowWorkspace>().Single().SelectedPage == page.Name
                    && Descendants(window.Content).OfType<TextBlock>().Any(text => text.Text == page.Name)
                    && Descendants(window.Content).OfType<SettingsRow>().Any());
            }
            foreach (string legacy in new[] { "启动选项", "画板选项", "手势选项", "显示器", "高级选项", "自动选项", "墨迹识别选项", "外观选项", "PPT 选项", "关于 Ink Canvas Artistry" })
            {
                window.SelectCategory(legacy);
                Check("legacy navigation remains available " + legacy,
                    Descendants(window.Content).OfType<WindowWorkspace>().Single().SelectedPage == SettingsCategoryCatalog.Resolve(legacy));
            }
            Check("browsing reorganized pages preserves settings", JsonSerializer.Serialize(services.Settings) == original);

            var toggles = new (string Label, Func<bool> Get)[]
            {
                ("上传图片时将画质压缩至 1080P 及以下", () => services.Settings.Canvas.IsCompressPicturesUploaded),
                ("浮动工具栏显示文字注释", () => services.Settings.Appearance.IsEnableDisPlayFloatBarText),
                ("浮动工具栏背景色", () => services.Settings.Appearance.IsColorfulViewboxFloatingBar),
                ("按钮背景透明", () => services.Settings.Appearance.IsTransparentButtonBackground),
                ("显示退出按钮", () => services.Settings.Appearance.IsShowExitButton),
                ("显示橡皮按钮", () => services.Settings.Appearance.IsShowEraserButton),
                ("显示隐藏控制按钮", () => services.Settings.Appearance.IsShowHideControlButton),
                ("显示左右切换按钮", () => services.Settings.Appearance.IsShowLRSwitchButton),
                ("显示手指模式切换按钮", () => services.Settings.Appearance.IsShowModeFingerToggleSwitch),
                ("关闭批注模式后隐藏墨迹", () => services.Settings.PowerPoint.HideStrokesAfterLeavingAnnotation),
                ("显示画笔光标", () => services.Settings.Canvas.IsShowCursor),
                ("在调色盘窗口中显示 启用笔尖模式 切换按钮", () => services.Settings.Appearance.IsEnableDisPlayNibModeToggler),
                ("关闭软件时二次弹窗确认", () => services.Settings.Advanced.IsSecondConfimeWhenShutdownApp),
                ("进入“希沃轻白板”时自动收纳", () => services.Settings.Automation.IsAutoFoldInEasiNote3C),
                ("进入“鸿合视频展台”时自动收纳", () => services.Settings.Automation.IsAutoFoldInHiteCamera),
                ("进入“文香白板”时自动收纳", () => services.Settings.Automation.IsAutoFoldInWxBoardMain),
                ("进入“中原旧白板”时自动收纳", () => services.Settings.Automation.IsAutoFoldInOldZyBoard),
                ("自动查杀希沃“PPT 小工具”", () => services.Settings.Automation.IsAutoKillPptService),
                ("自动查杀 希沃白板5", () => services.Settings.Automation.IsAutoKillEasiNote),
                ("切换到鼠标模式后自动清屏", () => services.Settings.Automation.IsAutoClearWhenExitingWritingMode),
            };
            foreach (var item in toggles)
            {
                var toggle = Find<ToggleSwitch>(item.Label);
                bool before = item.Get();
                var peer = new ToggleSwitchAutomationPeer(toggle);
                ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)).Toggle();
                bool changed = item.Get() != before;
                Check(item.Label, changed && toggle.IsOn == item.Get());
                ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)).Toggle();
                Check(item.Label + " restores", item.Get() == before);
            }
            var autoUpdate = Find<ToggleSwitch>("自动检查更新");
            bool updateBefore = autoUpdate.IsOn;
            autoUpdate.IsOn = false;
            Check("silent update disables with a visible reason", !Find<ToggleSwitch>("静默更新").IsEnabled
                && !string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(Find<ToggleSwitch>("静默更新"))));
            autoUpdate = Find<ToggleSwitch>("自动检查更新");
            autoUpdate.IsOn = true;
            Check("silent update enables when its prerequisite returns", Find<ToggleSwitch>("静默更新").IsEnabled);
            Find<ToggleSwitch>("自动检查更新").IsOn = updateBefore;
            Find<ToggleSwitch>("进入“希沃白板”时自动收纳").IsOn = false;
            Find<ToggleSwitch>("进入“希沃轻白板”时自动收纳").IsOn = false;
            Check("annotation exception requires a parent autofold rule", !Find<ToggleSwitch>("自动收纳忽略桌面EN批注窗口").IsEnabled);
            Find<ToggleSwitch>("进入“希沃轻白板”时自动收纳").IsOn = true;
            Check("light whiteboard rule keeps the desktop annotation exception disabled", !Find<ToggleSwitch>("自动收纳忽略桌面EN批注窗口").IsEnabled);
            Find<ToggleSwitch>("进入“希沃白板”时自动收纳").IsOn = true;
            Check("annotation exception enables immediately", Find<ToggleSwitch>("自动收纳忽略桌面EN批注窗口").IsEnabled);
            Find<ToggleSwitch>("进入“希沃白板”时自动收纳").IsOn = false;
            Find<ToggleSwitch>("进入“希沃轻白板”时自动收纳").IsOn = false;
            string missingPath = Path.Combine(directory, "missing-cleanup-directory");
            Find<TextBox>("墨迹、截图自动保存位置").Text = missingPath;
            await Task.Delay(450);
            Check("cleanup disables when saved directory is missing", !Find<ToggleSwitch>("定期自动删除超过保存时间的墨迹、截图文件").IsEnabled);
            Directory.CreateDirectory(missingPath);
            await Task.Delay(2200);
            Check("cleanup enables when saved directory becomes available", Find<ToggleSwitch>("定期自动删除超过保存时间的墨迹、截图文件").IsEnabled);
            Find<ToggleSwitch>("定期自动删除超过保存时间的墨迹、截图文件").IsOn = true;
            var retention = Find<ComboBox>("保存时间设置（Days）");
            retention.SelectedIndex = 3;
            Check("retention days save through the real combo", services.Settings.Automation.AutoDelSavedFilesDaysThreshold == 7);
            Find<ToggleSwitch>("定期自动删除超过保存时间的墨迹、截图文件").IsOn = false;
            Check("retention editor disables when cleanup is off", !Find<ComboBox>("保存时间设置（Days）").IsEnabled);
            var showEraser = Find<ToggleSwitch>("显示橡皮按钮");
            showEraser.IsOn = false;
            Check("eraser visibility applies immediately", bar.Bar.GetToolButton(InkTool.Eraser).Visibility == Visibility.Collapsed);
            showEraser.IsOn = true;
            Check("eraser visibility restores", bar.Bar.GetToolButton(InkTool.Eraser).Visibility == Visibility.Visible);

            var eraser = Find<ComboBox>("橡皮模式");
            eraser.SelectedIndex = 2;
            Check("whole stroke eraser applies", services.OverlayInk.EraserMode == InkEraserMode.WholeStroke);
            eraser.SelectedIndex = 1;
            Check("area eraser applies", services.OverlayInk.EraserMode == InkEraserMode.Area);
            eraser.SelectedIndex = 0;
            Find<ToggleSwitch>("使用硬件压感").IsOn = false;
            Check("hardware pressure setting reaches live controller", !services.OverlayInk.UseHardwarePressure);
            Find<ToggleSwitch>("使用硬件压感").IsOn = true;
            Find<Slider>("停笔吸附等待时间 (毫秒)").Value = 800;
            Check("hold duration reaches live controller", services.OverlayInk.RecognitionOptions.HoldMilliseconds == 800);
            Find<ToggleSwitch>("自动对齐水平、垂直与 45° 方向").IsOn = false;
            Check("alignment editor follows its toggle", !Find<Slider>("方向吸附容差 (度)").IsEnabled
                && !services.OverlayInk.RecognitionOptions.AutoAlign);
            Find<ToggleSwitch>("自动对齐水平、垂直与 45° 方向").IsOn = true;
            Find<Slider>("方向吸附容差 (度)").Value = 7;
            Check("alignment tolerance reaches live controller", services.OverlayInk.RecognitionOptions.AlignmentAngle == 7);
            var palette = (EraserPalette)bar.Bar.CreateEraserPanelContent();
            var lasso = Descendants(palette).OfType<Button>().Single(button => AutomationProperties.GetName(button).Contains("套索擦"));
            Invoke(lasso);
            services.ApplySettings();
            Check("manual eraser choice survives other settings", services.OverlayInk.EraserMode == InkEraserMode.Lasso);

            var boardColor = Find<ToggleSwitch>("白板背景配色");
            boardColor.IsOn = true;
            Check("whiteboard color applies", services.Board.Background == BoardBackground.White);
            boardColor.IsOn = false;
            Check("blackboard color applies", services.Board.Background == BoardBackground.Black);

            CheckShortcut("工具栏缩放快捷值 125%", "浮动工具栏 UI 缩放 (%)", 125, () => services.Settings.Appearance.FloatingBarScale);
            CheckShortcut("画板缩放快捷值 80%", "画板 UI 缩放 (%)", 80, () => services.Settings.Appearance.BlackboardScale);
            Find<Slider>("浮动工具栏底部边距 (像素)").Value = 40;
            CheckShortcut("底部边距快捷值 重置为 100", "浮动工具栏底部边距 (像素)", 100, () => services.Settings.Appearance.FloatingBarBottomMargin);

            Find<Slider>("荧光笔粗细").Value = 120;
            services.OverlayInk.Tool = InkTool.Highlighter;
            Check("extended highlighter width applies", services.OverlayInk.CurrentAttributes.BaseWidth == 60);
            services.OverlayInk.Tool = InkTool.Pen;
            Check("highlighter width does not change pen", services.OverlayInk.CurrentAttributes.BaseWidth == services.Settings.Canvas.InkWidth);
            var alpha = Find<Slider>("荧光笔透明度");
            alpha.Value = 180;
            Check("highlighter alpha applies", services.OverlayInk.CurrentAttributes.HighlighterAlpha == 180);
            window.SelectCategory("书写与擦除");
            var penStyles = Find<ComboBox>("笔迹风格");
            penStyles.SelectedIndex = 7;
            penStyles.IsDropDownOpen = true;
            await Task.Delay(100);
            bool inkWashIconVisible = VisualTreeHelper.GetOpenPopupsForXamlRoot(((FrameworkElement)window.Content).XamlRoot)
                .SelectMany(popup => Descendants(popup.Child)).OfType<PenStyleIcon>()
                .Any(icon => AutomationProperties.GetName(icon) == "水墨笔锋");
            penStyles.IsDropDownOpen = false;
            checks.Add(new { check = "ink wash setting shows its icon and applies", selectedIndex = penStyles.SelectedIndex,
                persistedIndex = services.Settings.Canvas.InkStyle, liveStyle = services.OverlayInk.CurrentAttributes.TaperKind.ToString(),
                hasIcon = inkWashIconVisible,
                passed = penStyles.SelectedIndex == 7 && services.Settings.Canvas.InkStyle == 7
                    && services.OverlayInk.CurrentAttributes.TaperKind == InkCanvas.Core.Ink.TaperKind.InkWash
                    && inkWashIconVisible });
            penStyles.SelectedIndex = 0;
            var path = Find<TextBox>("墨迹、截图自动保存位置");
            path.Text = Path.Combine(directory, "typed-path");
            await Task.Delay(450);
            Check("typed path automatically saves", services.Store.Load().Automation.AutoSavedStrokesLocation == path.Text);
            Invoke(Find<Button>("将自动保存目录设置为文档目录"));
            Check("path shortcut refreshes editor", path.Text == Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

            foreach (int themeIndex in new[] { 1, 0, 2 })
            {
                Find<ComboBox>("颜色主题").SelectedIndex = themeIndex;
                await Task.Delay(100);
                Check("theme rebuild preserves edited values " + themeIndex,
                    Find<Slider>("浮动工具栏 UI 缩放 (%)").Value == 125
                    && Find<Slider>("荧光笔透明度").Value == 180);
            }

            string lastPath = Path.Combine(directory, "close-before-debounce");
            Find<TextBox>("墨迹、截图自动保存位置").Text = lastPath;
            window.Close();
            window = null;
            Check("closing flushes pending text", services.Store.Load().Automation.AutoSavedStrokesLocation == lastPath);
            string persisted = JsonSerializer.Serialize(services.Store.Load());
            Check("all edited values persist", persisted == JsonSerializer.Serialize(services.Settings));
            Open();
            await Task.Delay(150);
            var restoredEraserMode = Find<ComboBox>("橡皮模式").SelectedIndex;
            checks.Add(new { check = "reopening restores saved controls", lastPathRestored = Find<TextBox>("墨迹、截图自动保存位置").Text == lastPath,
                toolbarScale = Find<Slider>("浮动工具栏 UI 缩放 (%)").Value, eraserMode = restoredEraserMode,
                passed = Find<TextBox>("墨迹、截图自动保存位置").Text == lastPath
                    && Find<Slider>("浮动工具栏 UI 缩放 (%)").Value == 125 && restoredEraserMode == 3 });
            foreach (int themeIndex in new[] { 0, 1 })
            {
                Find<ComboBox>("颜色主题").SelectedIndex = themeIndex;
                await Task.Delay(120);
                foreach (int width in new[] { 1000, 640, 1280 })
                {
                    window.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(width, 760));
                    await Task.Delay(100);
                    foreach (string category in SettingsCategoryCatalog.Pages.Select(page => page.Name))
                    {
                        window.SelectCategory(category);
                        ((FrameworkElement)window.Content).UpdateLayout();
                        await Task.Delay(40);
                        var rows = Descendants(window.Content).OfType<SettingsRow>().ToArray();
                        Check("unavailable settings explain their prerequisite " + category, rows.All(row =>
                            !Descendants(row.Editor).OfType<Control>().Any(control => !control.IsEnabled)
                            || !string.IsNullOrWhiteSpace(row.Description)));
                        var invalid = rows.Where(row => row.Children.OfType<FrameworkElement>().Any(child =>
                        {
                            var bounds = child.TransformToVisual(row).TransformBounds(new Windows.Foundation.Rect(0, 0, child.ActualWidth, child.ActualHeight));
                            return bounds.Left < -1 || bounds.Top < -1 || bounds.Right > row.ActualWidth + 1 || bounds.Bottom > row.ActualHeight + 1;
                        })).Select(row => row.Title).ToArray();
                        checks.Add(new { check = "settings rows fit responsive page", themeIndex, width, category,
                            passed = invalid.Length == 0, invalid, rows = rows.Length, stacked = rows.Count(row => row.IsStacked) });
                        if (category == "外观与工具栏")
                        {
                            Check("scale presets share the slider card " + themeIndex + " " + width, rows
                                .Where(row => row.Title.Contains("UI 缩放")).All(row => Descendants(row).OfType<Button>().Count() == 5));
                        }
                        if (width is 640 or 1280 && category is "常规" or "外观与工具栏" or "书写与擦除")
                            await SaveSettingsVisual(window, Path.Combine(directory, $"settings-{category}-{themeIndex}-{width}.png"));
                    }
                }
            }

            void Open()
            {
                window = new SettingsWindow(services, updater);
                window.SettingsChanged += bar.Bar.RefreshSettingsVisuals;
                window.Activate();
            }
            T Find<T>(string label) where T : DependencyObject
            {
                T FindOnPage() => Descendants(window.Content).OfType<T>().SingleOrDefault(control => AutomationProperties.GetName(control) == label);
                var result = FindOnPage();
                if (result is not null) return result;
                foreach (string category in SettingsCategoryCatalog.Pages.Select(page => page.Name))
                {
                    window.SelectCategory(category);
                    ((FrameworkElement)window.Content).UpdateLayout();
                    result = FindOnPage();
                    if (result is not null) return result;
                }
                throw new InvalidOperationException("Missing setting " + label);
            }
            void Check(string name, bool passed) => checks.Add(new { check = name, passed });
            void CheckShortcut(string button, string slider, double expected, Func<double> get)
            {
                Invoke(Find<Button>(button));
                Check(button, Find<Slider>(slider).Value == expected && get() == expected);
            }
        }
        catch (Exception exception) { checks.Add(new { check = "settings regression exception", passed = false, error = exception.ToString() }); }
        finally
        {
            window?.Close();
            bar.Close();
            File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Exit();
        }
    }

    private static async Task CheckModuleSettingsAsync(SettingsWindow window, AppServices services, List<object> checks)
    {
        string[] Navigation() => Descendants(window.Content).OfType<NavigationView>().Single().MenuItems
            .OfType<NavigationViewItem>().Select(item => item.Content?.ToString()).ToArray();
        void Check(string name, bool passed) => checks.Add(new { check = name, passed });
        Check("uninstalled plugin settings have no sidebar placeholders", !Navigation().Contains("AI 助手")
            && !Navigation().Contains("屏幕内容跟随") && !Navigation().Contains("实验功能"));
        Check("uninstalled plugin controls are excluded from search", !window.SearchSettings("API 连接与模型").Any()
            && !window.SearchSettings("批注跟随正文滚动").Any());

        int views = 0;
        bool original = services.Settings.Startup.IsFoldAtStartup;
        var page = new ModuleSettingsPage("diagnostics.settings", "诊断插件", Glyphs.Tools, "插件检索词", builder =>
        {
            builder.AddToggle("诊断插件开关", () => services.Settings.Startup.IsFoldAtStartup,
                value => services.Settings.Startup.IsFoldAtStartup = value);
            builder.AddView("诊断插件视图", () => { views++; return new TextBlock { Text = "插件视图" }; });
        }) { Aliases = new[] { "旧诊断入口" } };
        using var runtime = new ModuleRuntime();
        runtime.AddService<IModuleSettingsRegistry>(services.ModuleSettings);
        runtime.Register(new() { Id = "diagnostics.settings", Name = "诊断插件", Version = "1.0.0", EntryType = "Diagnostic.Settings" },
            () => new SettingsDiagnosticModule(page));
        runtime.Register(new() { Id = "diagnostics.disabled", Name = "已停用插件", Version = "1.0.0", EntryType = "Diagnostic.Settings" },
            () => new SettingsDiagnosticModule(page with { Id = "diagnostics.disabled", Name = "已停用插件" }));
        runtime.Start(new HashSet<string> { "diagnostics.disabled" });
        await Task.Delay(120);
        Check("running plugin registers one sidebar page", Navigation().Count(name => name == page.Name) == 1);
        Check("disabled plugin registers no sidebar page", !Navigation().Contains("已停用插件"));
        var hit = window.SearchSettings("诊断插件开关").Single();
        Check("plugin search metadata is indexed lazily", views == 0 && window.SearchSettings("插件检索词").Count == 2);
        window.SelectCategory("旧诊断入口"); await Task.Delay(80);
        Check("plugin aliases select their registered page", Descendants(window.Content).OfType<WindowWorkspace>().Single().SelectedPage == page.Name);
        var toggle = Descendants(window.Content).OfType<ToggleSwitch>().Single();
        toggle.IsOn = !original;
        Check("plugin controls use shared settings persistence", services.Store.Load().Startup.IsFoldAtStartup == !original);
        toggle.IsOn = original;
        Check("search index preserves the active plugin view", window.SearchSettings("诊断插件开关").Count == 1
            && ReferenceEquals(toggle, Descendants(window.Content).OfType<ToggleSwitch>().Single()) && views == 1);
        Check("plugin result navigates to its control", window.NavigateToSetting(hit));
        runtime.Invoke<ISettingsDiagnosticService>(_ => throw new InvalidOperationException("diagnostic failure"));
        await Task.Delay(120);
        Check("failed plugin removes its settings registration", services.ModuleSettings.Pages.Count == 0 && !Navigation().Contains(page.Name));
        Check("removing the active plugin selects a populated core page", Descendants(window.Content).OfType<WindowWorkspace>().Single().SelectedPage == "模块与插件");
        Check("removed plugin leaves no searchable or stale target", !window.SearchSettings("诊断插件开关").Any() && !window.NavigateToSetting(hit));
        Check("empty plugin navigation group is removed", !Descendants(window.Content).OfType<NavigationView>().Single().MenuItems
            .OfType<NavigationViewItemHeader>().Any(item => item.Content?.ToString() == "插件设置"));

        using var failed = new ModuleRuntime();
        failed.AddService<IModuleSettingsRegistry>(services.ModuleSettings);
        failed.Register(new() { Id = "diagnostics.startup", Name = "启动失败插件", Version = "1.0.0", EntryType = "Diagnostic.Settings" },
            () => new SettingsDiagnosticModule(page, failStartup: true));
        failed.Start(new HashSet<string>());
        await Task.Delay(100);
        Check("startup rollback removes partially registered settings", services.ModuleSettings.Pages.Count == 0 && !Navigation().Contains(page.Name));
        window.SelectCategory("常规");
    }

    private interface ISettingsDiagnosticService { }

    private sealed class SettingsDiagnosticModule(ModuleSettingsPage page, bool failStartup = false) : IApplicationModule, ISettingsDiagnosticService
    {
        public void Start(IModuleContext context)
        {
            context.RegisterCleanup(context.GetService<IModuleSettingsRegistry>().Register(page).Dispose);
            if (failStartup) throw new InvalidOperationException("diagnostic startup failure");
            context.Export<ISettingsDiagnosticService>(this);
        }
        public void Stop() { }
    }

    private static async Task CheckScrollViewportAsync(SettingsWindow window, string directory, List<object> checks)
    {
        foreach (int theme in new[] { 1, 2 })
        {
            WindowChrome.ApplyTheme(window, theme);
            foreach (int width in new[] { 640, 1230 })
            {
                window.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(width, 850));
                foreach (string category in new[] { "存储与缓存", "书写与擦除", "保存与备份" })
                {
                    window.SelectCategory(category);
                    await Task.Delay(100);
                    var workspace = Descendants(window.Content).OfType<WindowWorkspace>().Single();
                    var scroll = Descendants(workspace).OfType<ScrollViewer>().Single(view => view.Content is StackPanel or Border { Child: StackPanel });
                    scroll.ChangeView(null, scroll.ScrollableHeight / 2, null, true);
                    await Task.Delay(50);
                    var viewport = Descendants(scroll).OfType<ScrollContentPresenter>().First();
                    var bottom = viewport.TransformToVisual(scroll).TransformPoint(new(0, viewport.ActualHeight));
                    double gap = scroll.ActualHeight - bottom.Y;
                    checks.Add(new { check = "settings scroll viewport reaches the content area's bottom", theme, width, category, gap,
                        passed = gap >= -1 && gap <= 1 });
                    scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
                    await Task.Delay(50);
                    FrameworkElement page = scroll.Content is Border wrapper ? (FrameworkElement)wrapper.Child : (FrameworkElement)scroll.Content;
                    var end = page.TransformToVisual(viewport).TransformPoint(new(0, page.ActualHeight));
                    double endPadding = viewport.ActualHeight - end.Y;
                    checks.Add(new { check = "last setting remains reachable with breathing room at the page end", theme, width, category, endPadding,
                        passed = scroll.ScrollableHeight == 0 || endPadding >= 23 && endPadding <= 25 });
                    if (category != "存储与缓存") continue;
                    scroll.ChangeView(null, scroll.ScrollableHeight / 2, null, true);
                    await Task.Delay(50);
                    var frame = (WindowFrame)window.Content;
                    var material = frame.Background;
                    frame.Background = new SolidColorBrush(theme == 1 ? Windows.UI.Color.FromArgb(255, 243, 243, 243) : Windows.UI.Color.FromArgb(255, 32, 32, 32));
                    try { await BoardWindowRegression.SaveVisual(frame, Path.Combine(directory, $"storage-{theme}-{width}.png")); }
                    finally { frame.Background = material; }
                }
            }
        }
    }

    private static async Task SaveSettingsVisual(SettingsWindow window, string path)
    {
        var frame = (WindowFrame)window.Content;
        var material = frame.Background;
        // RenderTargetBitmap cannot capture the native Mica backdrop.
        frame.Background = new SolidColorBrush(frame.ActualTheme == ElementTheme.Dark
            ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 243, 243, 243));
        try { await BoardWindowRegression.SaveVisual(frame, path); }
        finally { frame.Background = material; }
    }

    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
}
