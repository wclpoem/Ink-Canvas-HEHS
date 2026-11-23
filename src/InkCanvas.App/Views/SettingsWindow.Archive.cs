using InkCanvas.App.Services;
using InkCanvas.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.App.Views;

public sealed partial class SettingsWindow
{
    private void BuildArchiveSettings()
    {
        AddSection("默认存档目录");
        _content.Children.Add(ArchiveDirectoryRow("手动存档默认目录", () => S.Archive.DefaultDirectory, path => S.Archive.DefaultDirectory = path));
        _content.Children.Add(ArchiveDirectoryRow("白板自动存档目录", () => S.Archive.AutomaticDirectory, path => S.Archive.AutomaticDirectory = path));
        _content.Children.Add(InfoRow("自动存档目录留空时，使用手动默认目录下的 AutoSave 子目录。保存、打开白板文件时将使用默认目录。"));
        _content.Children.Add(ArchiveDirectoryRow("墨迹、截图自动保存位置", () => S.Automation.AutoSavedStrokesLocation, path => S.Automation.AutoSavedStrokesLocation = path, shortcuts: PathShortcuts()));

        AddSection("白板自动保存");
        _content.Children.Add(ToggleRow("退出时自动保存完整白板", () => S.Archive.SaveOnExit, value => S.Archive.SaveOnExit = value,
            note: "退出白板、关闭软件或重启前，保存所有页面、纸型、墨迹、图片和视频。保存失败时保留白板并提示。"));
        _content.Children.Add(ToggleRow("定时自动保存完整白板", () => S.Archive.AutoSaveEnabled, value => S.Archive.AutoSaveEnabled = value,
            note: "在后台写入存档，书写或拖动期间稍后重试。"));
        _content.Children.Add(Require(ArchiveNumberRow("自动保存间隔（分钟）", () => S.Archive.AutoSaveMinutes, value => S.Archive.AutoSaveMinutes = value, 1, 120),
            () => S.Archive.AutoSaveEnabled ? null : "请先启用定时自动保存。"));
        _content.Children.Add(ToggleRow("仅在白板变化后自动保存", () => S.Archive.OnlyWhenChanged, value => S.Archive.OnlyWhenChanged = value,
            note: "包括页面、笔画、纸型、媒体位置和画布视图变化。"));
        _content.Children.Add(ToggleRow("跳过空白白板", () => S.Archive.SkipEmptyBoards, value => S.Archive.SkipEmptyBoards = value,
            note: "所有页面均没有墨迹、图片和视频时跳过自动保存。"));
        _content.Children.Add(ToggleRow("白板自动存档按日期分文件夹", () => S.Archive.UseDateFolders, value => S.Archive.UseDateFolders = value));
        _content.Children.Add(ArchiveNumberRow("保留最近自动存档份数", () => S.Archive.AutomaticCopiesToKeep, value => S.Archive.AutomaticCopiesToKeep = value, 1, 200));
        _content.Children.Add(InfoRow("仅清理带有 Ink Canvas 自动保存标记的白板存档，手动存档不计入份数。同名自动存档会生成新文件。"));

        AddSection("截图与墨迹存档");
        _content.Children.Add(ToggleRow("自动保存截图与墨迹为白板", () => S.Archive.ScreenCapture.Enabled, value => S.Archive.ScreenCapture.Enabled = value,
            note: "有墨迹时生成单页白板，截图置于底层，墨迹、图片和视频保留为独立对象，屏幕外墨迹也会保存。适用于 PPT 放映批注，并在使用本程序翻页前保存。"));
        _content.Children.Add(ToggleRow("普通批注模式也自动保存截图白板", () => S.Archive.ScreenCapture.IncludeOrdinaryAnnotation,
            value => S.Archive.ScreenCapture.IncludeOrdinaryAnnotation = value, note: "配合上方开关，在桌面及其他应用的屏幕批注中使用。"));
        _content.Children.Add(ArchiveDirectoryRow("截图白板存档目录", () => S.Archive.ScreenCapture.Directory, value => S.Archive.ScreenCapture.Directory = value));
        _content.Children.Add(InfoRow("目录留空时使用默认存档目录下的 ScreenArchives，沿用下方文件名模板及日期文件夹设置。截图背景会排除本程序的工具栏、墨迹和弹窗。"));
        _content.Children.Add(Require(ArchiveNumberRow("截图白板保存间隔（分钟）", () => S.Archive.ScreenCapture.IntervalMinutes,
            value => S.Archive.ScreenCapture.IntervalMinutes = value, 1, 120),
            () => S.Archive.ScreenCapture.Enabled ? null : "请先启用截图与墨迹自动存档。"));
        _content.Children.Add(ToggleRow("切换鼠标模式前保存截图白板", () => S.Archive.ScreenCapture.SaveOnMouseMode, value => S.Archive.ScreenCapture.SaveOnMouseMode = value));
        _content.Children.Add(ToggleRow("清屏前保存截图白板", () => S.Archive.ScreenCapture.SaveOnClear, value => S.Archive.ScreenCapture.SaveOnClear = value));
        _content.Children.Add(ToggleRow("退出程序前保存截图白板", () => S.Archive.ScreenCapture.SaveOnExit, value => S.Archive.ScreenCapture.SaveOnExit = value));
        _content.Children.Add(ToggleRow("仅在批注变化后保存截图白板", () => S.Archive.ScreenCapture.OnlyWhenChanged, value => S.Archive.ScreenCapture.OnlyWhenChanged = value));
        _content.Children.Add(ArchiveNumberRow("保留最近截图白板份数", () => S.Archive.ScreenCapture.CopiesToKeep, value => S.Archive.ScreenCapture.CopiesToKeep = value, 1, 200));
        _content.Children.Add(ButtonRow("立即保存截图白板", "保存当前屏幕及其墨迹。请先启用对应批注模式的自动保存开关，并在屏幕上书写。", async () =>
        {
            if (_shell is not ShellController shell) return;
            var result = await shell.SaveScreenArchiveAsync("手动屏幕存档", force: true);
            if (!_closed) ShowNotification(result is null ? _services.ScreenArchives.LastError ?? "当前模式未启用，或没有可保存的屏幕墨迹。" : "已保存到 " + result.Path,
                result is null ? InfoBarSeverity.Informational : InfoBarSeverity.Success);
        }, "立即保存"));

        AddSection("墨迹与截图保存");
        _content.Children.Add(ToggleRow("清屏时自动截图", () => S.Automation.IsAutoSaveStrokesAtClear,
            v => S.Automation.IsAutoSaveStrokesAtClear = v));
        _content.Children.Add(ToggleRow("截图分日期文件夹保存", () => S.Automation.IsSaveScreenshotsInDateFolders,
            v => S.Automation.IsSaveScreenshotsInDateFolders = v));
        _content.Children.Add(ToggleRow("截图时自动保存墨迹", () => S.Automation.IsAutoSaveStrokesAtScreenshot,
            v => S.Automation.IsAutoSaveStrokesAtScreenshot = v));
        _content.Children.Add(SliderRow("自动保存所需最少墨迹数（0 表示不限制）",
            () => S.Automation.MinimumAutomationStrokeNumber, 0, 10,
            v => S.Automation.MinimumAutomationStrokeNumber = (int)v));
        AddSection("历史文件清理");
        var retentionDays = OriginalMenuCatalog.AutoDeleteDays.Append(S.Automation.AutoDelSavedFilesDaysThreshold)
            .Where(day => day > 0).Distinct().Order().ToArray();
        _content.Children.Add(Require(ToggleRow("定期自动删除超过保存时间的墨迹、截图文件", () => S.Automation.AutoDelSavedFiles,
            value => S.Automation.AutoDelSavedFiles = value,
            note: "每小时清理保存目录中过期的墨迹存档和程序生成的截图，跳过链接目录。"), SaveDirectoryRequirement));
        _content.Children.Add(Require(ComboRow("保存时间设置（Days）", retentionDays.Select(day => day + " 天").ToArray(),
            () => Math.Max(0, Array.IndexOf(retentionDays, S.Automation.AutoDelSavedFilesDaysThreshold)),
            index => S.Automation.AutoDelSavedFilesDaysThreshold = retentionDays[index]),
            () => !S.Automation.AutoDelSavedFiles ? "请先启用定期自动删除。" : SaveDirectoryRequirement()));

        AddSection("文件名与存档信息");
        _content.Children.Add(TextRow("课程或存档标题", () => S.Archive.Title, value => S.Archive.Title = value));
        _content.Children.Add(TextRow("班级", () => S.Archive.ClassName, value => S.Archive.ClassName = value));
        _content.Children.Add(TextRow("教师", () => S.Archive.Teacher, value => S.Archive.Teacher = value));
        _content.Children.Add(TextRow("存档文件名模板", () => S.Archive.FileNameTemplate, value => S.Archive.FileNameTemplate = value));
        _content.Children.Add(InfoRow("支持 {title} 标题、{class} 班级、{teacher} 教师、{date} 日期、{time} 时间、{pages} 页数、{reason} 保存原因。日期使用 yyyy-MM-dd，时间使用 HH-mm-ss。"));
        var preview = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        _content.Children.Add(Searchable(Card(Row("文件名预览", preview, editorWidth: 360)), "文件名预览", "存档 命名 模板"));
        _refreshControls.Add(() =>
        {
            try { preview.Text = ArchivePolicy.FileName(S.Archive, DateTimeOffset.Now, _services.Board.PageCount, "退出白板"); }
            catch (Exception ex) { preview.Text = ex.Message; }
        });
        _content.Children.Add(InfoRow("存档同时记录保存时间、原因、页数、课程、班级、教师和程序版本，可随文件查看与恢复。"));

        AddSection("保存状态");
        var state = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        _content.Children.Add(Searchable(Card(state), "最近存档与自动保存状态", "时间 路径 大小 错误 下次自动保存"));
        _refreshControls.Add(() =>
        {
            var archives = _services.Archives;
            string last = archives.LastSave is { } saved
                ? $"最近存档 {saved.SavedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {saved.PageCount} 页 · {FormatBytes(saved.Bytes)}\n{saved.Path}" : "尚无存档记录。";
            string automatic = archives.LastAutomaticSave is { } auto
                ? $"\n最近自动保存 {auto.SavedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {auto.Reason}\n{auto.Path}" : "\n尚无白板自动保存记录。";
            state.Text = (archives.IsSaving ? "正在保存…\n" : "") + last + automatic
                + (archives.NextSaveUtc is { } next ? $"\n下次自动检查 {next.ToLocalTime():HH:mm:ss}" : "\n定时自动保存未启用。")
                + (archives.LastError is { } error ? "\n保存失败 · " + error : "")
                + (_services.AutoSave.LastPath is { } snapshot ? "\n最近墨迹快照 " + snapshot : "")
                + (_services.AutoSave.LastError is { } snapshotError ? "\n墨迹快照失败 · " + snapshotError : "");
            var screenArchives = _services.ScreenArchives;
            if (screenArchives.IsSaving) state.Text += "\n正在保存截图白板…";
            if (screenArchives.LastSave is { } screen) state.Text += $"\n最近截图白板 {screen.SavedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {FormatBytes(screen.Bytes)}\n{screen.Path}";
            if (screenArchives.LastError is { } screenError) state.Text += "\n截图白板保存失败 · " + screenError;
            if (screenArchives.NextSaveUtc is { } screenNext) state.Text += $"\n下次截图白板检查 {screenNext.ToLocalTime():HH:mm:ss}";
        });
        _content.Children.Add(ButtonRow("立即备份完整白板", "使用当前自动存档目录和文件名配置生成一份白板备份。", async () =>
        {
            try
            {
                var result = await _services.Archives.SaveAutomaticAsync("立即备份", force: true);
                if (!_closed) ShowNotification(result is null ? "白板为空，已跳过保存。" : "已保存到 " + result.Path);
            }
            catch (Exception ex) { if (!_closed) ShowNotification("存档失败 · " + ex.GetBaseException().Message, InfoBarSeverity.Error); }
        }, "立即保存"));
        _content.Children.Add(ButtonRow("打开白板自动存档目录", "查看保存的完整白板文件。", () =>
        {
            try { OpenStorageFolder(ArchivePolicy.AutomaticDirectory(S.Archive)); }
            catch (Exception ex) { ShowNotification(ex.Message, InfoBarSeverity.Error); }
        }, "打开文件夹"));
        foreach (var refresh in _refreshControls.ToArray()) refresh();
    }

    private UIElement ArchiveNumberRow(string title, Func<int> get, Action<int> set, int min, int max)
    {
        var number = new NumberBox { Minimum = min, Maximum = max, Value = get(), MinHeight = 40, Width = 180,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        Accessible(number, title);
        number.ValueChanged += (_, args) =>
        {
            if (_building || _synchronizingControls || !double.IsFinite(args.NewValue)) return;
            int value = (int)Math.Clamp(args.NewValue, min, max);
            if (get() == value) return;
            set(value); Commit();
        };
        _refreshControls.Add(() => number.Value = get());
        return Searchable(Card(Row(title, number)), title);
    }

    private UIElement ArchiveDirectoryRow(string title, Func<string> get, Action<string> set, FrameworkElement shortcuts = null)
    {
        var browse = new Button { Content = "选择文件夹", MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Right };
        Accessible(browse, title + " · 选择文件夹");
        browse.Click += async (_, _) =>
        {
            try
            {
                string initial = string.IsNullOrWhiteSpace(get()) ? S.Archive.DefaultDirectory : get();
                string selected = await ImageFilePicker.PickFolderAsync(WindowChrome.GetWorkArea(this), initial);
                if (_closed || selected is null) return;
                set(selected); Commit();
            }
            catch (Exception ex) { if (!_closed) ShowNotification("目录选择失败 · " + ex.Message, InfoBarSeverity.Error); }
        };
        FrameworkElement actions = browse;
        if (shortcuts is not null)
        {
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(browse);
            panel.Children.Add(shortcuts);
            actions = panel;
        }
        return TextRow(title, get, set, actions);
    }
}
