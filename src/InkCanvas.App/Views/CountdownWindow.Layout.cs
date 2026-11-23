using InkCanvas.App.Services;
using InkCanvas.Controls.Controls;
using InkCanvas.Controls.Lifecycle;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Theming;
using InkCanvas.Features.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Typography = Microsoft.UI.Xaml.Documents.Typography;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace InkCanvas.App.Views;

public sealed partial class CountdownWindow
{
    private const string CompactGlyph = "\uF608";
    private readonly Grid _root = new();
    private readonly Grid _expanded = new() { RowSpacing = 16, Margin = new Thickness(28, 16, 28, 24) };
    private readonly Grid _compact = new() { Margin = new Thickness(16), RowSpacing = 12, Visibility = Visibility.Collapsed };
    private readonly UISettings _uiSettings = new();
    private readonly List<(int Seconds, ToggleButton Button)> _presets = new();
    private readonly List<(TouchIconButton Button, bool Primary)> _buttons = new();
    private readonly List<TextBlock> _optionLabels = new();
    private readonly NumberBox[] _durationInputs = new NumberBox[3];
    private readonly CountdownRing _ring = new();
    private readonly TextBlock _time = Digits(58), _compactTime = Digits(56);
    private readonly TextBlock _faceHint = new() { Text = "点击时间可修改", FontSize = 12, Opacity = .6, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _status = new() { FontSize = 13 }, _compactStatus = new() { FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _endTime = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border _statusBadge = new() { CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 5, 12, 5), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Flyout _durationFlyout = new() { Placement = FlyoutPlacementMode.Bottom };
    private readonly ToggleButton _sound = new() { IsChecked = true }, _repeat = new();
    private readonly TextBlock _editError = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, FontSize = 12 };
    private WindowFrame _frame;
    private Viewbox _hero;
    private TouchIconButton _startButton, _compactStart, _timeButton, _compactTimeButton, _compactButton, _fullScreenButton, _fullScreenExit, _secondaryButton, _compactLap;
    private UIElement _presetStrip;
    internal bool EditorOpen => _editorOpen;
    private bool _editorOpen;
    private bool _narrowOptions, _narrowHeader;

    private void BuildLayout(int theme)
    {
        _expanded.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _expanded.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _expanded.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _expanded.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _modeHost.Visibility = Visibility.Collapsed; _expanded.Children.Add(_modeHost);
        _presetStrip = BuildPresets();
        var modes = new Grid(); modes.Children.Add(_presetStrip); modes.Children.Add(_lapPanel);
        Grid.SetRow(modes, 1); _expanded.Children.Add(modes);
        var face = new Grid { Width = 400, Height = 400 };
        face.Children.Add(_ring);
        _statusBadge.Child = _status;
        _timeButton = TimerButton("点击时间调整时长", null, ToggleEdit);
        _timeButton.Content = new Viewbox { Child = _time, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
        _timeButton.Width = 308; _timeButton.Height = 80;
        _timeButton.BorderThickness = new Thickness(0);
        _timeButton.CornerRadius = new CornerRadius(14);
        var center = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        center.Children.Add(_statusBadge); center.Children.Add(_timeButton);
        center.Children.Add(_faceHint);
        face.Children.Add(center);
        _hero = new Viewbox { Child = face, Stretch = Stretch.Uniform, MaxWidth = 520, MaxHeight = 520, Margin = new Thickness(12, 0, 12, 0) };
        Grid.SetRow(_hero, 2); _expanded.Children.Add(_hero);
        var footer = BuildControlDock(); Grid.SetRow(footer, 3); _expanded.Children.Add(footer);
        BuildCompact(); BuildDurationEditor();
        _fullScreenExit = TimerButton("退出全屏", Glyphs.FoldWpf, ToggleFullScreen);
        _fullScreenExit.HorizontalAlignment = HorizontalAlignment.Right; _fullScreenExit.VerticalAlignment = VerticalAlignment.Top;
        _fullScreenExit.Margin = new Thickness(12); _fullScreenExit.Visibility = Visibility.Collapsed;
        _root.Children.Add(_expanded); _root.Children.Add(_compact); _root.Children.Add(_fullScreenExit);
        Content = _root;
        WindowChrome.MakeStandard(this, 820, 740, topmost: false);
        WindowChrome.ApplyTheme(this, theme);
        _frame = WindowFrame.Attach(this, _root).UseMica();
        _compactButton = TimerButton("置顶小窗", CompactGlyph, ToggleCompact);
        _fullScreenButton = TimerButton("全屏演示", Glyphs.FitPage, ToggleFullScreen);
        foreach (var button in new[] { _compactButton, _fullScreenButton })
        { button.MinHeight = button.Height = 40; button.FontSize = 12; button.Padding = new Thickness(12, 0, 12, 0); button.BorderThickness = new Thickness(0); }
        _frame.Header.RightHeader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { _compactButton, _fullScreenButton } };
        ConfigureStopwatch();
        _minSize = new MinSizeHook(this, 480, 600);
        _root.ActualThemeChanged += (_, _) => ApplyPalette();
        _root.Loaded += (_, _) => ApplyPalette();
        DeferredLayout.Observe(_root, () =>
        {
            if (_disposed) return;
            PlaceTimerModes();
            bool options = _root.ActualWidth < 600, header = _root.ActualWidth < 700;
            if (_narrowOptions != options)
            {
                _narrowOptions = options;
                foreach (var label in _optionLabels) label.Visibility = options ? Visibility.Collapsed : Visibility.Visible;
            }
            if (_narrowHeader == header) return;
            _narrowHeader = header;
            foreach (var item in new[] { (_compactButton, "置顶小窗", CompactGlyph), (_fullScreenButton, "全屏演示", Glyphs.FitPage) })
            {
                if (header) { item.Item1.Content = Icon(item.Item3); item.Item1.Width = 44; }
                else { ClassroomControls.SetButtonContent(item.Item1, item.Item2, item.Item3); item.Item1.Width = double.NaN; }
            }
        });
        _root.KeyDown += OnKeyDown;
        AppWindow.Changed += PresenterChanged;
        Activated += (_, _) => { if (!_disposed) { SyncRefreshTimer(); RefreshTime(); } };
        _uiSettings.ColorValuesChanged += SystemColorsChanged;
        ApplyPalette();
    }

    private UIElement BuildPresets()
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(2) };
        foreach (int seconds in new[] { 30, 60, 180, 300, 600, 2400, 2700 })
        {
            var button = new ToggleButton { Content = seconds < 60 ? "30 秒" : $"{seconds / 60} 分钟", MinHeight = 44,
                MinWidth = 68, CornerRadius = new CornerRadius(22), Padding = new Thickness(14, 6, 14, 6), FontSize = 14 };
            AutomationProperties.SetName(button, seconds < 60 ? "30秒" : $"{seconds / 60}分钟");
            button.Click += (_, _) => { SetDuration(seconds); RefreshPresets(); };
            strip.Children.Add(button); _presets.Add((seconds, button));
        }
        return new ScrollViewer { Content = strip, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            ZoomMode = ZoomMode.Disabled, Padding = new Thickness(0, 4, 0, 4) };
    }

    private Grid BuildControlDock()
    {
        var footer = new Grid { RowSpacing = 20 };
        footer.RowDefinitions.Add(new() { Height = GridLength.Auto }); footer.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var dock = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center };
        var reset = TimerButton("复位", Glyphs.Restart, Reset); reset.Width = reset.Height = 56; reset.Content = Icon(Glyphs.Restart); reset.CornerRadius = new CornerRadius(28);
        _startButton = TimerButton("开始", Glyphs.Play, StartPause, primary: true); _startButton.Width = 164; _startButton.Height = 64; _startButton.CornerRadius = new CornerRadius(32);
        _secondaryButton = TimerButton("1 分钟", Glyphs.Add, AddTimeOrLap); _secondaryButton.Height = 56; _secondaryButton.CornerRadius = new CornerRadius(28);
        dock.Children.Add(reset); dock.Children.Add(_startButton); dock.Children.Add(_secondaryButton); footer.Children.Add(dock);
        var options = new Grid { ColumnSpacing = 12 };
        options.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); options.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        options.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var switches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        ConfigureOption(_sound, "结束提示音", "提示音", "\uEB4C"); ConfigureOption(_repeat, "循环计时", "循环", Glyphs.Replay);
        _sound.Unchecked += (_, _) => StopSound();
        switches.Children.Add(_sound); switches.Children.Add(_repeat); options.Children.Add(switches);
        Grid.SetColumn(_endTime, 2); options.Children.Add(_endTime);
        Grid.SetRow(options, 1); footer.Children.Add(options);
        return footer;
    }

    private void BuildCompact()
    {
        _compact.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _compact.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _compact.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new());
        header.Children.Add(_compactModeHost); Grid.SetColumn(_compactStatus, 1); header.Children.Add(_compactStatus);
        _compactStatus.TextTrimming = TextTrimming.CharacterEllipsis; _compactStatus.VerticalAlignment = VerticalAlignment.Center;
        _compact.Children.Add(header);
        _compactTimeButton = TimerButton("点击时间调整时长", null, ToggleEdit);
        _compactTimeButton.Content = new Viewbox { Child = _compactTime, Stretch = Stretch.Uniform };
        _compactTimeButton.BorderThickness = new Thickness(0); _compactTimeButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetRow(_compactTimeButton, 1); _compact.Children.Add(_compactTimeButton);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
        _compactStart = TimerButton("开始", Glyphs.Play, StartPause, primary: true); _compactStart.Width = 120;
        var reset = TimerButton("复位", Glyphs.Restart, Reset); reset.Content = Icon(Glyphs.Restart); reset.Width = 48;
        _compactLap = TimerButton("计次", Glyphs.Add, RecordLap); _compactLap.Content = Icon(Glyphs.Add); _compactLap.Width = 44;
        var restore = TimerButton("展开倒计时", Glyphs.FitPage, ToggleCompact); restore.Content = Icon(Glyphs.FitPage); restore.Width = 48;
        controls.Children.Add(reset); controls.Children.Add(_compactStart); controls.Children.Add(_compactLap); controls.Children.Add(restore);
        Grid.SetRow(controls, 2); _compact.Children.Add(controls);
    }

    private void BuildDurationEditor()
    {
        var content = new StackPanel { Width = 312, Spacing = 16 };
        content.Children.Add(new TextBlock { Text = "自定义时长", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var fields = new Grid { ColumnSpacing = 12 };
        for (int unit = 0; unit < 3; unit++)
        {
            int index = unit;
            fields.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var column = new StackPanel { Spacing = 8 };
            string label = new[] { "小时", "分钟", "秒" }[unit];
            column.Children.Add(new TextBlock { Text = label, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 13 });
            var increase = TimerButton("增加" + label, Glyphs.Add, () => AdjustInput(index, 1)); increase.Content = Icon(Glyphs.Add); column.Children.Add(increase);
            var input = new NumberBox { Minimum = 0, Maximum = unit == 0 ? 99 : 59, Value = 0, SmallChange = 1,
                FontSize = 24, MinHeight = 48, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
            AutomationProperties.SetName(input, label); _durationInputs[unit] = input; column.Children.Add(input);
            var decrease = TimerButton("减少" + label, "\uEBCB", () => AdjustInput(index, -1)); decrease.Content = Icon("\uEBCB"); column.Children.Add(decrease);
            Grid.SetColumn(column, unit); fields.Children.Add(column);
        }
        content.Children.Add(fields); content.Children.Add(_editError);
        var apply = TimerButton("确定", Glyphs.Play, ApplyDuration, primary: true); content.Children.Add(apply);
        _durationFlyout.Content = content;
        _durationFlyout.Opened += (_, _) => _editorOpen = true;
        _durationFlyout.Closed += (_, _) => _editorOpen = false;
    }

    internal void ToggleEdit()
    {
        if (_stopwatchMode || _session.State == CountdownState.Running) return;
        if (_editorOpen) { _durationFlyout.Hide(); return; }
        int seconds = ConfiguredSeconds;
        _durationInputs[0].Value = seconds / 3600; _durationInputs[1].Value = seconds / 60 % 60; _durationInputs[2].Value = seconds % 60;
        _editError.Visibility = Visibility.Collapsed;
        _durationFlyout.ShowAt(_mode == TimerView.Compact ? _compactTimeButton : _timeButton);
    }

    private void AdjustInput(int index, int change)
    {
        var input = _durationInputs[index];
        input.Value = Math.Clamp((double.IsFinite(input.Value) ? input.Value : 0) + change, input.Minimum, input.Maximum);
    }

    private void ApplyDuration()
    {
        if (_durationInputs.Any(input => !double.IsFinite(input.Value))) { ShowDurationError(); return; }
        int seconds = ((int)Math.Round(_durationInputs[0].Value) * 60 + (int)Math.Round(_durationInputs[1].Value)) * 60 + (int)Math.Round(_durationInputs[2].Value);
        if (seconds <= 0) { ShowDurationError(); return; }
        SetDuration(seconds);
    }

    private void ShowDurationError()
    {
        _editError.Text = "请设置至少 1 秒的时长"; _editError.Visibility = Visibility.Visible;
    }

    private TouchIconButton TimerButton(string label, string glyph, Action action, bool primary = false)
    {
        var button = new TouchIconButton { MinWidth = 44, MinHeight = 48, Padding = new Thickness(14, 8, 14, 8), FontSize = 16,
            CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), SelectionBackground = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        if (glyph is null) button.Content = label; else ClassroomControls.SetButtonContent(button, label, glyph);
        AutomationProperties.SetName(button, label); ToolTipService.SetToolTip(button, label);
        button.Click += (_, _) => action();
        _buttons.Add((button, primary));
        return button;
    }

    private static FontIcon Icon(string glyph) => new() { Glyph = glyph, FontFamily = IconFontStyle.Font, FontSize = 20, IsTextScaleFactorEnabled = false };
    private static TextBlock Digits(double size)
    {
        var text = new TextBlock { FontSize = size, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, IsTextScaleFactorEnabled = false };
        Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
        return text;
    }

    private void RefreshPresets()
    {
        int seconds = IsRunning ? _session.TotalSeconds : ConfiguredSeconds;
        foreach (var preset in _presets)
        {
            preset.Button.IsChecked = preset.Seconds == seconds;
            preset.Button.IsEnabled = _session.State != CountdownState.Running;
        }
    }

    private void RefreshState()
    {
        bool running = _stopwatchMode ? _stopwatch.IsRunning : _session.State == CountdownState.Running;
        bool paused = _stopwatchMode ? !_stopwatch.IsRunning && _stopwatch.Elapsed > TimeSpan.Zero : IsPaused;
        string state = _stopwatchMode ? running ? "正向计时中" : paused ? "已暂停" : "秒表就绪"
            : _session.State switch { CountdownState.Running => Round > 1 ? $"第 {Round} 轮倒计时中" : "倒计时中",
            CountdownState.Paused => "已暂停", CountdownState.Finished => "时间到", _ => "准备开始" };
        _status.Text = _compactStatus.Text = state;
        string action = running ? "暂停" : paused ? "继续" : !_stopwatchMode && _session.State == CountdownState.Finished ? "再来一次" : "开始";
        foreach (var button in new[] { _startButton, _compactStart })
        {
            ClassroomControls.SetButtonContent(button, action, running ? Glyphs.Pause : Glyphs.Play);
            AutomationProperties.SetName(button, action); ToolTipService.SetToolTip(button, action);
        }
        _timeButton.IsEnabled = _compactTimeButton.IsEnabled = !_stopwatchMode && !running;
        _presetStrip.Visibility = _sound.Visibility = _repeat.Visibility = _stopwatchMode ? Visibility.Collapsed : Visibility.Visible;
        _lapPanel.Visibility = _stopwatchMode && _lapItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _compactLap.Visibility = _stopwatchMode ? Visibility.Visible : Visibility.Collapsed; _compactLap.IsEnabled = _stopwatch.IsRunning;
        _compactStart.Width = _stopwatchMode ? 96 : 120;
        ClassroomControls.SetButtonContent(_secondaryButton, _stopwatchMode ? "计次" : "1 分钟", Glyphs.Add);
        AutomationProperties.SetName(_secondaryButton, _stopwatchMode ? "计次" : "加 1 分钟");
        ToolTipService.SetToolTip(_secondaryButton, _stopwatchMode ? "记录本次与累计用时" : "增加 1 分钟");
        _secondaryButton.IsEnabled = !_stopwatchMode || _stopwatch.IsRunning;
        _faceHint.FontSize = _stopwatchMode ? 24 : 12;
        if (!_stopwatchMode) _faceHint.Text = "点击时间可修改";
        _endTime.Text = _stopwatchMode ? (_lapItems.Count > 0 ? $"已记录 {_lapItems.Count} 次" : "")
            : _session.State == CountdownState.Running ? $"预计 {DateTime.Now.AddSeconds(_session.RemainingSeconds):HH:mm} 结束"
            : IsPaused ? "计时已暂停" : _session.State == CountdownState.Finished ? "本轮计时已结束" : "";
        RefreshPresets(); RefreshTime(); ApplyPalette();
    }

    private void SystemColorsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(() => { if (!_disposed) ApplyPalette(); });

    private void ApplyPalette()
    {
        bool dark = _root.ActualTheme == ElementTheme.Dark;
        var accent = _uiSettings.GetColorValue(dark ? UIColorType.AccentLight2 : UIColorType.AccentDark1);
        var ink = ColorContrast.Foreground(new InkColor(accent.A, accent.R, accent.G, accent.B));
        var accentText = new SolidColorBrush(Color.FromArgb(ink.A, ink.R, ink.G, ink.B));
        var foreground = ThemeBrushes.Resolve(_root, "IcTextPrimaryBrush");
        var secondary = ThemeBrushes.Resolve(_root, "IcTextSecondaryBrush");
        _root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _status.Foreground = _compactStatus.Foreground = secondary; _endTime.Foreground = secondary;
        _time.Foreground = _compactTime.Foreground = foreground;
        _statusBadge.Background = ThemeBrushes.Resolve(_root, "IcAccentSoftBrush");
        _editError.Foreground = new SolidColorBrush(dark ? Color.FromArgb(255, 255, 180, 171) : Color.FromArgb(255, 178, 45, 34));
        var warning = dark ? Color.FromArgb(255, 255, 185, 96) : Color.FromArgb(255, 186, 77, 0);
        _ring.ApplyPalette(_warning ? warning : accent, ThemeBrushes.Resolve(_root, "IcStrokeBrush"), _warning);
        foreach (var item in _buttons)
        {
            var button = item.Button;
            bool time = ReferenceEquals(button, _timeButton) || ReferenceEquals(button, _compactTimeButton);
            button.Background = item.Primary ? new SolidColorBrush(accent) : time || button.Height == 40
                ? new SolidColorBrush(Microsoft.UI.Colors.Transparent) : ThemeBrushes.Resolve(_root, "IcSurfaceAltBrush");
            button.ContentForeground = item.Primary ? accentText : foreground;
            button.BorderBrush = ThemeBrushes.Resolve(_root, "IcStrokeBrush");
            button.HoverBackground = item.Primary ? new SolidColorBrush(accent) : ThemeBrushes.Resolve(_root, "IcAccentSoftBrush");
            button.PressedBackground = button.HoverBackground;
            button.ApplyHoverResources(button.ContentForeground); button.RefreshVisual();
            if (button.Content is StackPanel content)
                foreach (var child in content.Children)
                {
                    if (child is TextBlock text) text.Foreground = button.ContentForeground;
                    if (child is FontIcon icon) icon.Foreground = button.ContentForeground;
                }
            else if (button.Content is FontIcon icon) icon.Foreground = button.ContentForeground;
        }
    }
}
