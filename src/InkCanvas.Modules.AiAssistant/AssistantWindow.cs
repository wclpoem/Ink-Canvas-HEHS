using System.Collections.ObjectModel;
using InkCanvas.Controls.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.Modules.AiAssistant;

public sealed partial class AssistantWindow : Window
{
    private readonly Grid _root = new() { Padding = new Thickness(16, 4, 16, 16), RowSpacing = 12 };
    private readonly ListView _conversation = new() { SelectionMode = ListViewSelectionMode.None,
        IsItemClickEnabled = false, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ObservableCollection<AssistantChatTurn> _turns = new();
    private readonly TextBox _input = new() { PlaceholderText = "向 AI 助手提出任务…", AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap, MinHeight = 76, MaxHeight = 180, BorderThickness = new Thickness(0) };
    private readonly StackPanel _attachments = new() { Spacing = 4 };
    private readonly ComboBox _chats = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0 };
    private readonly ToggleSwitch _thinking = new() { Header = "思考", OnContent = "开启", OffContent = "关闭" };
    private readonly ComboBox _effort = new() { Header = "思考强度", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _empty = new() { Text = "今天需要协助完成什么？", FontSize = 22,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 64, 16, 0) };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Visibility = Visibility.Collapsed };
    private readonly TextBlock _model = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _send = IconButton("发送 (Enter)", Symbol.Send);
    private readonly Button _stop = IconButton("停止生成", Symbol.Stop);
    private readonly List<Button> _attachmentButtons = new();
    private AssistantWorkspace _workspace;
    private AssistantChat _chat;
    private ScrollViewer _scroll;
    private bool _busy, _binding, _closed, _composing, _following = true, _scrollQueued;
    private bool _autoScrolling;
    private double _lastScrollOffset;
    public new event Action Closed;
    public event Action<AssistantChat> SendRequested;
    public event Action StopRequested;
    public event Action Changed;
    public event Action ConversationChanging;
    public event Action CaptureRequested;
    public event Action<Guid> RetryRequested;

    public AssistantWindow(int theme)
    {
        Title = "AI 助手";
        _root.RequestedTheme = theme switch { 1 => ElementTheme.Light, 2 => ElementTheme.Dark, _ => ElementTheme.Default };
        _conversation.ItemsSource = _turns;
        _conversation.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><ContentControl HorizontalContentAlignment="Stretch" /></DataTemplate>
            """);
        _conversation.ItemContainerStyle = (Style)XamlReader.Load("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ListViewItem">
                <Setter Property="HorizontalContentAlignment" Value="Stretch"/><Setter Property="Padding" Value="0"/>
                <Setter Property="Margin" Value="0,0,0,16"/>
            </Style>
            """);
        _conversation.ContainerContentChanging += BindContainer;
        _conversation.Loaded += (_, _) =>
        {
            _scroll = Descendants(_conversation).OfType<ScrollViewer>().FirstOrDefault();
            if (_scroll is not null) _scroll.ViewChanged += (_, _) =>
            {
                if (_autoScrolling || Math.Abs(_scroll.VerticalOffset - _lastScrollOffset) < .5) return;
                _lastScrollOffset = _scroll.VerticalOffset;
                _following = _scroll.ScrollableHeight - _scroll.VerticalOffset < 48;
            };
            ScrollToEnd();
        };
        BuildLayout(); Content = _root;
        _root.ActualThemeChanged += (_, _) => RefreshTheme();
        _root.Loaded += (_, _) => RefreshTheme();
        base.Closed += (_, _) => { _closed = true; CloseEdgeHandle(); Closed?.Invoke(); };
    }

    private void BindContainer(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer.ContentTemplateRoot is not ContentControl host)
        { if (!args.InRecycleQueue) args.RegisterUpdateCallback(BindContainer); return; }
        if (host.Content is not AssistantChatCard card)
        {
            card = new AssistantChatCard(); host.Content = card;
            card.RetryRequested += id => RetryRequested?.Invoke(id);
        }
        card.Bind(args.InRecycleQueue ? null : args.Item as AssistantChatTurn);
        args.Handled = true;
    }

    public void SetWorkspace(AssistantWorkspace workspace)
    {
        _workspace = workspace;
        if (_workspace.Active is null) _workspace.CreateChat();
        BindChat(_workspace.Active);
    }

    private void BindChat(AssistantChat chat)
    {
        _binding = true;
        _chat = chat; _workspace.ActiveId = chat.Id;
        _chats.ItemsSource = _workspace.Chats.OrderByDescending(item => item.UpdatedAt).ToArray();
        _chats.SelectedItem = chat;
        _turns.Clear(); foreach (var turn in chat.Turns) _turns.Add(turn);
        _input.Text = chat.Draft; _thinking.IsOn = chat.Thinking;
        _effort.SelectedIndex = chat.Effort switch { "low" => 0, "xhigh" => 2, _ => 1 };
        _effort.IsEnabled = chat.Thinking;
        _binding = false;
        RenderAttachments(); UpdateEmpty(); _following = true; ScrollToEnd();
    }

    public void RefreshChat(AssistantChat chat)
    {
        if (_closed || !ReferenceEquals(chat, _chat)) return;
        foreach (var turn in chat.Turns.Skip(_turns.Count)) _turns.Add(turn);
        _binding = true;
        _chats.ItemsSource = _workspace.Chats.OrderByDescending(item => item.UpdatedAt).ToArray();
        _chats.SelectedItem = chat;
        _input.Text = chat.Draft;
        _binding = false;
        RenderAttachments(); UpdateEmpty(); ScrollToEnd();
    }

    public void AddAttachment(AssistantAttachment attachment)
    { _chat.DraftAttachments.Add(attachment); RenderAttachments(); Changed?.Invoke(); }
    public void ReportError(string message)
    { _error.Text = message ?? ""; _error.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible; }
    public void SetModel(string model) => _model.Text = model;
    public void SetBusy(bool busy)
    {
        _busy = busy; _send.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        _stop.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in _attachmentButtons) button.IsEnabled = !busy;
    }
    private void Send()
    {
        if (_busy || _chat is null || string.IsNullOrWhiteSpace(_input.Text) && _chat.DraftAttachments.Count == 0) return;
        _chat.Draft = _input.Text;
        ReportError(""); _following = true; SendRequested?.Invoke(_chat);
    }
    private void UpdateEmpty() => _empty.Visibility = _turns.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    private void ScrollToEnd()
    {
        if (_closed || _collapsed || !_following || _scrollQueued || _turns.Count == 0) return;
        _scrollQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _scrollQueued = false;
            if (!_closed && _following && _turns.Count > 0)
            {
                _autoScrolling = true;
                try
                {
                    _conversation.ScrollIntoView(_turns[^1]);
                    if (_scroll is not null)
                    {
                        _scroll.UpdateLayout(); _scroll.ChangeView(null, _scroll.ScrollableHeight, null, true);
                        _lastScrollOffset = _scroll.VerticalOffset;
                    }
                }
                finally { _autoScrolling = false; }
            }
        });
    }
    public void RefreshTurn(AssistantChatTurn turn) { turn.Refresh(); ScrollToEnd(); }
    private void RefreshTheme()
    {
        _input.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _error.Foreground = ThemeBrushes.Resolve(_root, "SystemFillColorCriticalBrush");
        _model.Foreground = ThemeBrushes.Resolve(_root, "IcTextSecondaryBrush");
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
