using System.Diagnostics;
using InkCanvas.App.Services;
using InkCanvas.Controls.Animation;
using InkCanvas.Controls.Controls;
using InkCanvas.Controls.Theming;
using InkCanvas.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace InkCanvas.App.Views;

/// <summary>Standard settings window with on-demand category pages.</summary>
public sealed partial class SettingsWindow : Window
{
    private static FontFamily IconFont => IconFontStyle.Font;
    private static readonly Style SettingsCardStyle = (Style)XamlReader.Load("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Border">
            <Setter Property="Background" Value="{ThemeResource CardBackgroundFillColorDefaultBrush}" />
            <Setter Property="BorderBrush" Value="{ThemeResource CardStrokeColorDefaultBrush}" />
        </Style>
        """);
    private static readonly string[] SilenceTimes = BuildSilenceTimes();
    private readonly AppServices _services;
    private readonly UpdateCoordinator _updater;
    private readonly IShellActions _shell;
    private StackPanel _content;
    private WindowWorkspace _workspace;
    private TextBlock _updateStatus;
    private readonly List<Action> _refreshControls = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _notificationTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _textCommitTimer;
    private bool _synchronizingControls, _building, _closed, _committing, _rebuildPending;
    private int _theme;
    private InfoBar _notification;
    private readonly MinSizeHook _minSize;

    public SettingsWindow(AppServices services, UpdateCoordinator updater = null, IShellActions shell = null)
        : this(services, updater, shell, null) { }

    public SettingsWindow(AppServices services, UpdateCoordinator updater, IShellActions shell, string rosterDirectory)
    {
        _services = services;
        _rosterDirectory = rosterDirectory ?? LotteryWindow.DataDir;
        services.Plugins.Changed += RefreshExtensionsPage;
        services.Modules.Changed += RefreshExtensionsPage;
        services.ModuleSettings.Changed += RefreshModuleSettings;
        services.ExtensionUpdates.Changed += RefreshRepositoryUpdates;
        _updater = updater;
        if (_updater is not null) _updater.StatusChanged += RefreshUpdateStatus;
        _shell = shell;
        Title = "Ink Canvas 设置";
        _notificationTimer = DispatcherQueue.CreateTimer();
        _notificationTimer.Interval = TimeSpan.FromSeconds(3);
        _notificationTimer.IsRepeating = false;
        _notificationTimer.Tick += (_, _) => _notification.IsOpen = false;
        _textCommitTimer = DispatcherQueue.CreateTimer();
        _textCommitTimer.Interval = TimeSpan.FromMilliseconds(300);
        _textCommitTimer.IsRepeating = false;
        _textCommitTimer.Tick += (_, _) => Commit();
        _availabilityTimer = DispatcherQueue.CreateTimer();
        _availabilityTimer.Interval = TimeSpan.FromSeconds(2);
        _availabilityTimer.Tick += (_, _) => { if (!_closed && !_building && !_committing) RefreshControls(); };
        _availabilityTimer.Start();
        InitializeSearch();
        WindowChrome.MakeStandard(this, topmost: false);
        RebuildContent();
        _minSize = new MinSizeHook(this, 640, 360);
        AppWindow.Closing += OnRosterSettingsClosing;
        Closed += (_, _) =>
        {
            _closed = true;
            _rosterEditor?.Close();
            _services.Plugins.Changed -= RefreshExtensionsPage;
            _services.Modules.Changed -= RefreshExtensionsPage;
            _services.ModuleSettings.Changed -= RefreshModuleSettings;
            _services.ExtensionUpdates.Changed -= RefreshRepositoryUpdates;
            if (_updater is not null) _updater.StatusChanged -= RefreshUpdateStatus;
            _notificationTimer.Stop();
            _textCommitTimer.Stop();
            _availabilityTimer.Stop();
            CloseSearch();
            _minSize.Dispose();
            _services.SaveSettings();
        };
    }

    private AppSettings S => _services.Settings;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _availabilityTimer;
    private int BrushTheme => _theme == 0
        ? (Application.Current.RequestedTheme == ApplicationTheme.Dark ? 2 : 1) : _theme;
    public event Action SettingsChanged;
    internal void SelectCategory(string category) => _workspace.SelectPage(ResolveCategory(category));

    private void Commit()
    {
        if (_closed || _building || _committing || _synchronizingControls) return;
        _committing = true;
        try
        {
            _textCommitTimer.Stop();
            _services.ApplySettings();
            _services.SaveSettings();
            if (S.Appearance.Theme != _theme) QueueRebuild();
            else RefreshControls();
            SettingsChanged?.Invoke();
        }
        finally { _committing = false; }
    }

    private void QueueRebuild()
    {
        if (_rebuildPending) return;
        _rebuildPending = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _rebuildPending = false;
            if (!_closed) RebuildContent();
        });
    }

    private void RefreshControls()
    {
        _synchronizingControls = true;
        try { foreach (var refresh in _refreshControls) refresh(); }
        finally { _synchronizingControls = false; }
    }

    private void RebuildContent()
    {
        var navigation = _workspace?.NavigationStack ?? new[] { "常规" };
        string searchText = _search?.Text ?? "";
        _theme = S.Appearance.Theme;
        _searchIndexed = false;
        _searchEntries.Clear();
        _moduleSettingsPages = _services.ModuleSettings.Pages;
        _workspace = new WindowWorkspace { IsTitleBarAutoPaddingEnabled = false, IsBackNavigationEnabled = true };
        _workspace.Resources["NavigationViewHeaderMargin"] = new Thickness(24, 24, 24, 12);
        _workspace.Resources["NavigationViewContentBackground"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _workspace.Resources["NavigationViewContentGridBorderBrush"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        string group = null;
        foreach (var page in SettingsCategoryCatalog.Pages)
        {
            if (group != page.Group)
            {
                group = page.Group;
                _workspace.AddGroup(group);
            }
            _workspace.AddPage(page.Name, page.Glyph, () => BuildPage(page.Name));
            if (page.Name == "关于与更新") AddGuidePages();
        }
        if (_moduleSettingsPages.Count > 0) _workspace.AddGroup("插件设置");
        foreach (var page in _moduleSettingsPages)
            _workspace.AddPage(page.Name, page.Glyph, () => BuildPage(page.Name));
        _workspace.SearchBox = CreateSearchBox(searchText);
        _notification = new InfoBar { IsOpen = false, IsClosable = true, Severity = InfoBarSeverity.Success, Margin = new Thickness(24) };
        var root = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(_workspace);
        _updateCard = new UpdateStatusCard(BrushTheme) { Margin = new Thickness(24, 8, 24, 16), MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Stretch };
        _updateCard.CancelRequested += () =>
        { if (_services.ExtensionUpdates.IsBusy && _updater?.IsBusy != true) _services.ExtensionUpdates.Cancel(); else _updater?.Cancel(); };
        Grid.SetRow(_updateCard, 1);
        root.Children.Add(_updateCard);
        RefreshUpdateStatus();
        root.KeyDown += SearchKeyDown;
        _notification.VerticalAlignment = VerticalAlignment.Bottom;
        root.Children.Add(_notification);
        WindowFrame.Attach(this, root).UseMica();
        WindowChrome.ApplyTheme(this, _theme);
        _workspace.RestoreNavigation(navigation.Where(Categories.Contains).ToArray());
        if (_workspace.SelectedPage is null) _workspace.SelectPage("模块与插件");
    }

    private UIElement BuildPage(string category)
    {
        _building = true;
        try
        {
            _buildingCategory = category;
            if (!_indexingSearch && category != "模块与插件") { _repositoryStatus = null; _repositoryRefreshers.Clear(); }
            if (!_indexingSearch) _storagePageGeneration++;
            if (!_indexingSearch) _updateStatus = null;
            if (!_indexingSearch) { ResetSearchHighlight(); _searchTargets.Clear(); }
            _refreshControls.Clear();
            _content = new StackPanel { Spacing = 4, MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Stretch };
            BuildCategory(category);
            return _content;
        }
        finally { _building = false; }
    }

    private void ShowNotification(string text, InfoBarSeverity severity = InfoBarSeverity.Success)
    {
        _notification.Severity = severity;
        _notification.Message = text;
        _notification.IsOpen = true;
        _notificationTimer.Stop();
        _notificationTimer.Start();
    }

    // ---- 顶部卡片：重启软件 / 重置设置 / 关闭软件 ----------------------------

    private UIElement BuildTopCard()
    {
        var text = new TextBlock
        {
            Text = "设置更改将自动保存，部分设置需要重启软件后生效",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
            Foreground = SecondaryText(),
        };
        var buttons = new Grid
        {
            ColumnSpacing = 8,
        };
        for (int index = 0; index < 3; index++) buttons.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        buttons.Children.Add(TopCardButton("重启软件", "\uF1A0", 16, RestartApp));
        buttons.Children.Add(TopCardButton("重置设置", "\uF191", 18, ResetSettings));
        buttons.Children.Add(TopCardButton("关闭软件", "\uF619", 18, ExitApp));
        for (int index = 0; index < buttons.Children.Count; index++) Grid.SetColumn((FrameworkElement)buttons.Children[index], index);

        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(text);
        panel.Children.Add(buttons);

        return Searchable(Card(panel), "重置设置 / 重启软件 / 关闭软件");
    }

    private Button TopCardButton(string label, string glyph, double glyphSize, Action onClick)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontFamily = IconFont,
            FontSize = glyphSize,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = PopupBrushes.Foreground(BrushTheme),
        });
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = PopupBrushes.Foreground(BrushTheme),
        });
        var button = new Button
        {
            Content = row,
            MinWidth = 0,
            MinHeight = 40,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        Accessible(button, label);
        button.Click += (_, _) => onClick();
        return button;
    }

    private void RestartApp()
    {
        if (_shell is ShellController controller) { controller.Restart(); return; }
        // 原版 BtnRestart_Click：Process.Start(exe, "-m") 后退出。-m 在移植版被忽略（无互斥），保留参数以对齐原版。
        try
        {
            string exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) throw new InvalidOperationException("缺少程序路径");
            if (_services.ExtensionInstalls.Pending.Count > 0) { using var helper = Features.Update.UpdateService.LaunchRestart(); }
            else Process.Start(new ProcessStartInfo(exe, "-m") { UseShellExecute = true });
        }
        catch
        {
            ShowNotification("重启失败：无法启动新的程序实例。");
            return;
        }
        ExitApp();
    }

    private void ResetSettings()
    {
        S.ResetToRecommendation();
        // 原版重置后强制开启开机自启（ToggleSwitchRunAtStartup.IsOn = true）。
        Platform.System.AutoStartService.SetEnabled(true, Environment.ProcessPath ?? "InkCanvas.exe");
        _services.ApplySettings();
        _services.SaveSettings();
        SettingsChanged?.Invoke();
        // 与主题切换一致：延迟到下一调度帧重建，避免在按钮 Click 事件内拆毁自身。
        QueueRebuild();
    }

    private void ExitApp()
    {
        // ShellController.Exit：保存、清理、Dispose 后退出；绕过退出确认（原版 CloseIsFromButton）。
        if (_shell is not null) { _shell.Exit(); return; }
        if (_updater?.ExitForInstall is not null) { _updater.ExitForInstall(); return; }
        Close();
        Application.Current.Exit();
    }

    private void BuildCategory(string category)
    {
        switch (category)
        {
            case "常规": BuildGeneralSettings(); break;
            case "书写与擦除": BuildWritingSettings(); break;
            case "画布与识别": BuildCanvasSettings(); break;
            case "触控与手势": BuildTouchSettings(); break;
            case "外观与工具栏": BuildAppearanceSettings(); break;
            case "演示与课件": BuildPresentationSettings(); break;
            case "抽奖与点名": BuildLotterySettings(); break;
            case "模块与插件": BuildExtensionsCategory(); break;
            case "保存与备份": BuildArchiveSettings(); break;
            case "存储与缓存": BuildStorageSettings(); break;
            case "关于与更新": BuildAboutSettings(); break;
            case "操作指南": BuildGuide(); break;
            case "快捷键": BuildShortcutGuide(); break;
            case "书写与选择": BuildWritingGuide(); break;
            default:
                if (_moduleSettingsPages.FirstOrDefault(page => page.Name == category) is { } page)
                    BuildModuleSettings(page);
                break;
        }
    }

    private static string[] BuildSilenceTimes()
    {
        var times = new string[72];
        for (int i = 0; i < times.Length; i++)
            times[i] = $"{i * 20 / 60:00}:{i * 20 % 60:00}";
        return times;
    }

    private void RefreshUpdateStatus()
    {
        if (_closed) return;
        if (_updateStatus is not null) _updateStatus.Text = (_updater?.Status ?? "更新服务未启动")
            + (_updater?.LastCheckUtc is DateTime lastCheck ? "\n上次检查 " + lastCheck.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "");
        if (_updateCard is null) return;
        var progress = _updater?.Progress;
        bool active = _updater?.IsBusy == true;
        string status = _updater?.Status ?? "更新服务未启动";
        bool canCancel = _updater?.CanCancel == true;
        var extensions = _services.ExtensionUpdates;
        if (!active && (extensions.IsBusy || extensions.Progress.Stage is Features.Update.UpdateStage.Failed or Features.Update.UpdateStage.Waiting))
        { progress = extensions.Progress; active = extensions.IsBusy; status = extensions.Status; canCancel = active && progress.Stage != Features.Update.UpdateStage.Installing; }
        _updateCard.Visibility = active || progress?.Stage is Features.Update.UpdateStage.Failed or Features.Update.UpdateStage.Waiting
            ? Visibility.Visible : Visibility.Collapsed;
        string details = progress?.Source;
        if (progress?.DownloadedBytes > 0)
            details = string.Join(" · ", new[] { details, FormatBytes(progress.DownloadedBytes)
                + (progress.TotalBytes > 0 ? " / " + FormatBytes(progress.TotalBytes) : ""),
                progress.BytesPerSecond > 0 ? FormatBytes((long)progress.BytesPerSecond) + "/秒" : null }.Where(value => value is not null));
        _updateCard.Update(status, details, progress?.Fraction,
            active && progress?.Stage is Features.Update.UpdateStage.Checking or Features.Update.UpdateStage.Downloading
                or Features.Update.UpdateStage.Verifying or Features.Update.UpdateStage.Preparing, canCancel);
        if (!_building && !_committing) RefreshControls();
    }

    private FrameworkElement PathShortcuts()
    {
        var panel = new Grid { ColumnSpacing = 8 };
        panel.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var drive = new Button { Content = "D:\\Ink Canvas", MinHeight = 40, MinWidth = 0, Padding = new Thickness(8, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Stretch };
        Accessible(drive, "将自动保存目录设置为 D:\\Ink Canvas");
        drive.Click += (_, _) => { S.Automation.AutoSavedStrokesLocation = @"D:\Ink Canvas"; Commit(); };
        var documents = new Button { Content = "文档目录", MinHeight = 40, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        Accessible(documents, "将自动保存目录设置为文档目录");
        documents.Click += (_, _) =>
        {
            S.Automation.AutoSavedStrokesLocation = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            Commit();
        };
        panel.Children.Add(drive);
        Grid.SetColumn(documents, 1);
        panel.Children.Add(documents);
        return panel;
    }

    private FrameworkElement QuickValues(string label, double[] values, Action<double> set,
        Func<double, string> format = null)
    {
        var row = new Grid { ColumnSpacing = 4 };
        foreach (double value in values)
        {
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            string text = format?.Invoke(value) ?? $"{value:0}%";
            var button = new Button { Content = text, MinHeight = 40, MinWidth = 0, FontSize = 12,
                Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Stretch };
            Accessible(button, $"{label} {text}");
            button.Click += (_, _) => { set(value); Commit(); };
            Grid.SetColumn(button, row.Children.Count);
            row.Children.Add(button);
        }
        return row;
    }

    private UIElement LinkRow(string url, string credit)
    {
        var panel = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        var link = new HyperlinkButton
        {
            Content = new TextBlock { Text = url, TextWrapping = TextWrapping.Wrap },
            NavigateUri = new Uri(url),
            Padding = new Thickness(0),
            MinHeight = 40,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Accessible(link, url);
        panel.Children.Add(link);
        panel.Children.Add(new TextBlock
        {
            Text = credit,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = SecondaryText(),
        });
        return Card(panel);
    }

    // ---- row helpers ------------------------------------------------------

    private SolidColorBrush SecondaryText() => new(PopupBrushes.IsDark(BrushTheme)
        ? Color.FromArgb(0xA8, 255, 255, 255)
        : Color.FromArgb(0x9E, 0, 0, 0));

    private Border Card(UIElement child) => new()
    {
        Style = SettingsCardStyle,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(16),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Child = child,
    };

    private UIElement ToggleRow(string header, Func<bool> get, Action<bool> set,
        bool enabled = true, string disabledReason = null, string note = null)
    {
        var toggle = new ToggleSwitch
        {
            IsOn = get(),
            OnContent = "开",
            OffContent = "关",
            IsEnabled = enabled,
            MinWidth = 0,
            MinHeight = 40,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Accessible(toggle, header, enabled ? null : disabledReason);
        toggle.Toggled += (_, _) =>
        {
            if (_building || _synchronizingControls || !toggle.IsEnabled || toggle.IsOn == get()) return;
            set(toggle.IsOn);
            Commit();
        };
        _refreshControls.Add(() => toggle.IsOn = get());
        return RequireSetting(header, Searchable(Card(Row(header, toggle, note ?? (enabled ? null : disabledReason))), header, note ?? disabledReason));
    }

    private UIElement SliderRow(string header, Func<double> get, double min, double max,
        Action<double> set, double step = 1, double[] presets = null, string presetsLabel = null, Func<double, string> formatPreset = null)
    {
        var readout = new TextBlock
        {
            Text = get().ToString("0.##"),
            MinWidth = 48,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = PopupBrushes.Foreground(BrushTheme),
        };
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            StepFrequency = step,
            Value = Math.Clamp(get(), min, max),
            MinWidth = 100,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Accessible(slider, header, $"范围 {min:0.##} 到 {max:0.##}，步长 {step:0.##}");
        slider.ValueChanged += (_, e) =>
        {
            readout.Text = e.NewValue.ToString("0.##");
            if (_building || _synchronizingControls || !slider.IsEnabled || Math.Abs(e.NewValue - get()) < .0001) return;
            set(e.NewValue);
            Commit();
        };
        _refreshControls.Add(() =>
        {
            slider.Value = Math.Clamp(get(), min, max);
            readout.Text = get().ToString("0.##");
        });
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(slider, 0);
        Grid.SetColumn(readout, 1);
        row.Children.Add(slider);
        row.Children.Add(readout);
        var editor = new StackPanel { Spacing = 8, Children = { row } };
        if (presets is not null) editor.Children.Add(QuickValues(presetsLabel, presets, set, formatPreset));
        var card = Searchable(Card(Row(header, editor, editorWidth: 300)), header);
        if (presets is not null) Searchable(card, presetsLabel, string.Join(" ", presets.Select(value => formatPreset?.Invoke(value) ?? $"{value:0}%")));
        return RequireSetting(header, card);
    }

    private UIElement ComboRow(string header, IReadOnlyList<string> options, Func<int> get, Action<int> set, Func<int, UIElement> optionContent = null)
    {
        var combo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 0,
            MinHeight = 40,
        };
        for (int index = 0; index < options.Count; index++)
        {
            if (optionContent is null) combo.Items.Add(options[index]);
            else
            {
                var item = new ComboBoxItem { Content = optionContent(index) };
                AutomationProperties.SetName(item, options[index]);
                combo.Items.Add(item);
            }
        }
        // Set the index after items are present and before subscribing: WinUI otherwise
        // coerces it back to -1, and the initial change must not commit.
        combo.SelectedIndex = Math.Clamp(get(), 0, options.Count - 1);
        Accessible(combo, header);
        combo.SelectionChanged += (_, _) =>
        {
            if (_building || _synchronizingControls || !combo.IsEnabled || combo.SelectedIndex < 0 || combo.SelectedIndex == get()) return;
            set(combo.SelectedIndex);
            Commit();
        };
        _refreshControls.Add(() => combo.SelectedIndex = Math.Clamp(get(), 0, options.Count - 1));
        return RequireSetting(header, Searchable(Card(Row(header, combo, editorWidth: 240)), header, string.Join(" ", options)));
    }

    private UIElement TextRow(string header, Func<string> get, Action<string> set, FrameworkElement shortcuts = null)
    {
        var box = new TextBox
        {
            Text = get() ?? string.Empty,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 0,
            MinHeight = 40,
        };
        Accessible(box, header);
        box.TextChanging += (_, _) =>
        {
            if (_building || _synchronizingControls || box.Text == get()) return;
            set(box.Text);
            _textCommitTimer.Stop();
            _textCommitTimer.Start();
        };
        box.LostFocus += (_, _) =>
        {
            if (!_synchronizingControls && _textCommitTimer.IsRunning) Commit();
        };
        _refreshControls.Add(() =>
        {
            string text = get() ?? string.Empty;
            if (box.Text != text) box.Text = text;
        });
        var editor = new StackPanel { Spacing = 8, Children = { box } };
        if (shortcuts is not null) editor.Children.Add(shortcuts);
        return Searchable(Card(Row(header, editor, editorWidth: 320)), header);
    }

    private UIElement ButtonRow(string header, string description, Action onClick, string actionLabel = null)
    {
        var button = new Button
        {
            Content = actionLabel ?? (header switch { "检查更新" => "检查更新", "版本列表" or "更新日志" or "操作指南" => "查看",
                "打开用户插件目录" => "打开文件夹", "重新绑定当前批注位置" => "重新绑定", _ => "打开" }),
            MinWidth = 100,
            MinHeight = 40,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Accessible(button, header, description);
        button.Click += (_, _) => { if (button.IsEnabled) onClick(); };
        return RequireSetting(header, Searchable(Card(Row(header, button, description)), header, description));
    }

    private UIElement InfoRow(string text) => new TextBlock
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = SecondaryText(),
        FontSize = 12,
        LineHeight = 18,
        Margin = new Thickness(4, 6, 4, 10),
    };

    private SettingsRow Row(string header, FrameworkElement control, string note = null, double editorWidth = 0)
        => new(header, control, BrushTheme, note, editorWidth);

    private void AddSection(string title)
    {
        _content.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = PopupBrushes.Foreground(BrushTheme), Margin = new Thickness(4, _content.Children.Count == 0 ? 0 : 20, 0, 6) });
    }

    private static void Accessible(DependencyObject control, string name, string helpText = null)
    {
        AutomationProperties.SetName(control, name);
        if (!string.IsNullOrWhiteSpace(helpText)) AutomationProperties.SetHelpText(control, helpText);
    }

    private static string OptionalText(OptionalOperation operation) => operation switch
    {
        OptionalOperation.Yes => "是",
        OptionalOperation.No => "否",
        _ => "每次询问",
    };
}
