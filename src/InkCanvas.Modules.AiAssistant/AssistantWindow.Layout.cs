using InkCanvas.Controls.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace InkCanvas.Modules.AiAssistant;

public sealed partial class AssistantWindow
{
    private void BuildLayout()
    {
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new Grid { ColumnSpacing = 4 };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(_chats); AutomationProperties.SetName(_chats, "对话历史");
        _chats.SelectionChanged += (_, _) =>
        {
            if (_binding || _chats.SelectedItem is not AssistantChat chat || ReferenceEquals(chat, _chat)) return;
            ConversationChanging?.Invoke(); BindChat(chat); Changed?.Invoke();
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var create = IconButton("新建对话", Symbol.Add);
        create.Click += (_, _) => { ConversationChanging?.Invoke(); BindChat(_workspace.CreateChat()); Changed?.Invoke(); _input.Focus(FocusState.Programmatic); };
        var more = IconButton("对话管理", Symbol.More);
        var menu = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = "重命名对话" };
        rename.Click += async (_, _) =>
        {
            var chat = _chat;
            var name = new TextBox { Text = chat.Title, MaxLength = 80 };
            var dialog = new ContentDialog { XamlRoot = _root.XamlRoot, Title = "重命名对话", Content = name,
                PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(name.Text)) return;
            chat.Title = name.Text.Trim(); chat.UpdatedAt = DateTimeOffset.Now; RefreshChat(chat); Changed?.Invoke();
        };
        var delete = new MenuFlyoutItem { Text = "删除对话" };
        delete.Click += async (_, _) =>
        {
            var chat = _chat;
            var dialog = new ContentDialog { XamlRoot = _root.XamlRoot, Title = "删除对话", Content = "删除「" + chat.Title + "」及其附件记录？",
                PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            ConversationChanging?.Invoke(); _workspace.Chats.Remove(chat);
            BindChat(_workspace.Chats.LastOrDefault() ?? _workspace.CreateChat()); Changed?.Invoke();
        };
        menu.Items.Add(rename); menu.Items.Add(delete); menu.Items.Add(new MenuFlyoutSeparator());
        void Menu(string text, Action action) { var item = new MenuFlyoutItem { Text = text }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Menu("停靠左侧", () => DockPanel(false)); Menu("停靠右侧", () => DockPanel(true)); Menu("收起侧栏", CollapseToEdge);
        more.Flyout = menu;
        _pinButton = IconButton("取消窗口置顶", Symbol.Pin); _pinButton.Click += (_, _) => TogglePin();
        actions.Children.Add(create); actions.Children.Add(_pinButton); actions.Children.Add(more);
        Grid.SetColumn(actions, 1); header.Children.Add(actions); _root.Children.Add(header);
        var body = new Grid(); body.Children.Add(_conversation); body.Children.Add(_empty);
        AutomationProperties.SetName(_conversation, "助手对话"); Grid.SetRow(body, 1); _root.Children.Add(body);
        var composer = new StackPanel { Spacing = 8 }; composer.Children.Add(_error);
        composer.Children.Add(new ScrollViewer { Content = _attachments, MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var input = new StackPanel { Spacing = 4 }; input.Children.Add(_input);
        var row = new Grid(); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var add = IconButton("添加图片或文件", Symbol.Attach); add.Click += async (_, _) => await PickAttachmentsAsync();
        var capture = IconButton("添加当前画布", Symbol.Camera); capture.Click += (_, _) => CaptureRequested?.Invoke();
        _attachmentButtons.Add(add); _attachmentButtons.Add(capture);
        var reason = IconButton("思考设置", Symbol.Emoji2);
        reason.Content = new FontIcon { Glyph = Glyphs.Hint, FontFamily = IconFontStyle.Font, FontSize = 20 };
        var settings = new StackPanel { Spacing = 12, Width = 200 }; settings.Children.Add(_thinking);
        foreach (string label in new[] { "低", "高", "最高" }) _effort.Items.Add(label);
        settings.Children.Add(_effort); reason.Flyout = new Flyout { Content = settings };
        _thinking.Toggled += (_, _) =>
        {
            _effort.IsEnabled = _thinking.IsOn;
            if (_binding || _chat is null) return;
            _chat.Thinking = _thinking.IsOn; Changed?.Invoke();
        };
        _effort.SelectionChanged += (_, _) =>
        {
            if (_binding || _chat is null) return;
            _chat.Effort = _effort.SelectedIndex switch { 0 => "low", 2 => "xhigh", _ => "high" }; Changed?.Invoke();
        };
        buttons.Children.Add(add); buttons.Children.Add(capture); buttons.Children.Add(reason); row.Children.Add(buttons);
        var send = new Grid(); send.Children.Add(_send); send.Children.Add(_stop); Grid.SetColumn(send, 1); row.Children.Add(send);
        _send.Click += (_, _) => Send(); _stop.Click += (_, _) => StopRequested?.Invoke(); _stop.Visibility = Visibility.Collapsed;
        input.Children.Add(row);
        var border = new Border { Padding = new Thickness(8), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Child = input };
        border.Loaded += (_, _) => { border.Background = ThemeBrushes.Resolve(border, "IcSurfaceAltBrush"); border.BorderBrush = ThemeBrushes.Resolve(border, "IcStrokeBrush"); };
        border.ActualThemeChanged += (_, _) => { border.Background = ThemeBrushes.Resolve(border, "IcSurfaceAltBrush"); border.BorderBrush = ThemeBrushes.Resolve(border, "IcStrokeBrush"); };
        composer.Children.Add(border); composer.Children.Add(_model); Grid.SetRow(composer, 2); _root.Children.Add(composer);
        AutomationProperties.SetName(_input, "消息内容");
        _input.TextChanged += (_, _) => { if (_binding || _chat is null) return; _chat.Draft = _input.Text; Changed?.Invoke(); };
        _input.TextCompositionStarted += (_, _) => _composing = true;
        _input.TextCompositionEnded += (_, _) => DispatcherQueue.TryEnqueue(() => _composing = false);
        _input.KeyDown += (_, args) =>
        {
            bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (args.Key != VirtualKey.Enter || shift || _composing) return;
            Send(); args.Handled = true;
        };
    }

    internal static TouchIconButton IconButton(string name, Symbol symbol)
    {
        var button = new TouchIconButton { Width = 40, Height = 40, MinWidth = 40, MinHeight = 40,
            CornerRadius = new CornerRadius(8), Padding = new Thickness(8), BorderThickness = new Thickness(0), Content = new SymbolIcon(symbol) };
        AutomationProperties.SetName(button, name); ToolTipService.SetToolTip(button, name); return button;
    }
}
