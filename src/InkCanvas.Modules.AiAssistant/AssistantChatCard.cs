using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using InkCanvas.Controls.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace InkCanvas.Modules.AiAssistant;

internal sealed class AssistantChatCard : UserControl
{
    private readonly TextBlock _question = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 14 };
    private readonly Border _questionHost;
    private readonly StackPanel _files = new() { Spacing = 4 };
    private readonly StackPanel _answer = new() { Spacing = 8 };
    private readonly TextBlock _status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _fallback = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly Expander _tools = new() { Header = "工具记录", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _toolRows = new() { Spacing = 8 };
    private readonly Button _copy = AssistantWindow.IconButton("复制回答", Symbol.Copy);
    private readonly Button _retry = AssistantWindow.IconButton("重试本轮", Symbol.Refresh);
    private AssistantChatTurn _turn;
    private string _rendered;
    private readonly List<string> _keys = new();
    private int _toolCount = -1;
    public event Action<Guid> RetryRequested;

    public AssistantChatCard()
    {
        var body = new StackPanel { Spacing = 12, Padding = new Thickness(2, 4, 2, 4) };
        var question = new StackPanel { Spacing = 8, Children = { _question, _files } };
        _questionHost = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(12), Margin = new Thickness(24, 0, 0, 0), Child = question };
        body.Children.Add(_questionHost); body.Children.Add(_status); body.Children.Add(_tools);
        body.Children.Add(_answer); body.Children.Add(_fallback); body.Children.Add(_error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Children = { _copy, _retry } }; body.Children.Add(actions);
        _tools.Content = _toolRows;
        _copy.Click += (_, _) =>
        {
            if (string.IsNullOrEmpty(_turn?.Answer)) return;
            var data = new DataPackage(); data.SetText(_turn.Answer); Clipboard.SetContent(data);
        };
        _retry.Click += (_, _) => { if (_turn is not null) RetryRequested?.Invoke(_turn.Id); };
        Content = body;
        Loaded += (_, _) => { if (_turn is not null) { _turn.PropertyChanged -= OnChanged; _turn.PropertyChanged += OnChanged; } Refresh(); ApplyTheme(); };
        Unloaded += (_, _) => { if (_turn is not null) _turn.PropertyChanged -= OnChanged; };
        ActualThemeChanged += (_, _) => ApplyTheme();
    }

    public void Bind(AssistantChatTurn turn)
    {
        if (ReferenceEquals(_turn, turn)) return;
        if (_turn is not null) _turn.PropertyChanged -= OnChanged;
        _turn = turn; _rendered = null; _keys.Clear(); _answer.Children.Clear(); _files.Children.Clear(); _toolCount = -1;
        if (turn is null) return;
        turn.PropertyChanged += OnChanged;
        _question.Text = turn.Question;
        _question.Visibility = string.IsNullOrWhiteSpace(turn.Question) ? Visibility.Collapsed : Visibility.Visible;
        foreach (var attachment in turn.Attachments)
        {
            var button = new Button { Content = attachment.Name, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left, MinHeight = 40 };
            ToolTipService.SetToolTip(button, attachment.IsImage ? attachment.Name : attachment.Path);
            if (attachment.IsImage)
            {
                var image = new Image { MaxHeight = 320, MaxWidth = 300, Stretch = Stretch.Uniform };
                var flyout = new Flyout { Content = image };
                button.Flyout = flyout;
                flyout.Opened += async (_, _) =>
                {
                    try
                    {
                        var bitmap = new BitmapImage();
                        using var stream = new InMemoryRandomAccessStream();
                        byte[] bytes = Convert.FromBase64String(attachment.Image[(attachment.Image.IndexOf(',') + 1)..]);
                        await stream.WriteAsync(bytes.AsBuffer()); stream.Seek(0);
                        await bitmap.SetSourceAsync(stream); image.Source = bitmap;
                    }
                    catch (Exception) { image.Source = null; }
                };
                flyout.Closed += (_, _) => image.Source = null;
            }
            _files.Children.Add(button);
        }
        _questionHost.Visibility = string.IsNullOrWhiteSpace(turn.Question) && turn.Attachments.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        Refresh();
    }
    private void OnChanged(object sender, PropertyChangedEventArgs args) => Refresh();
    private void Refresh()
    {
        if (_turn is null) return;
        _status.Text = _turn.State == "running" ? string.IsNullOrEmpty(_turn.Activity) ? "正在处理…" : _turn.Activity
            : _turn.State == "stopped" ? "已停止" : "";
        _status.Visibility = _status.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _fallback.Text = _turn.Fallback; _fallback.Visibility = string.IsNullOrEmpty(_turn.Fallback) ? Visibility.Collapsed : Visibility.Visible;
        _error.Text = _turn.Error; _error.Visibility = string.IsNullOrEmpty(_turn.Error) ? Visibility.Collapsed : Visibility.Visible;
        _copy.IsEnabled = !string.IsNullOrWhiteSpace(_turn.Answer);
        _retry.Visibility = _turn.State is "error" or "stopped" && _turn.Tools.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_toolCount != _turn.Tools.Count)
        {
            _toolCount = _turn.Tools.Count; _toolRows.Children.Clear();
            foreach (var tool in _turn.Tools) _toolRows.Children.Add(new TextBlock { Text = tool.Tool + "\n" + tool.Result,
                FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            _tools.Visibility = _toolCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        if (_rendered == _turn.Answer) return;
        _rendered = _turn.Answer;
        var blocks = Core.AiAssistant.AssistantText.ParseMarkdown(_rendered ?? "");
        var texts = blocks.Select(block => block.Kind == Core.AiAssistant.MarkdownBlockKind.Table
            ? string.Join("\n", block.Rows.Select((row, index) => "| " + string.Join(" | ", row) + " |"
                + (index == 0 ? "\n| " + string.Join(" | ", row.Select(_ => "---")) + " |" : "")))
            : block.Kind == Core.AiAssistant.MarkdownBlockKind.Code ? "```\n" + block.Text + "\n```"
            : block.Kind == Core.AiAssistant.MarkdownBlockKind.Heading ? new string('#', Math.Clamp(block.Level, 1, 6)) + " " + block.Text
            : block.Kind == Core.AiAssistant.MarkdownBlockKind.ListItem ? "- " + block.Text : block.Text).ToArray();
        int same = 0;
        while (same < _keys.Count && same < texts.Length && _keys[same] == texts[same]) same++;
        while (_answer.Children.Count > same) _answer.Children.RemoveAt(_answer.Children.Count - 1);
        if (_keys.Count > same) _keys.RemoveRange(same, _keys.Count - same);
        foreach (string text in texts.Skip(same))
        {
            _answer.Children.Add(RenderBlock(text)); _keys.Add(text);
        }
    }
    private UIElement RenderBlock(string text)
    {
        var segments = Core.AiAssistant.AssistantText.SplitFormulas(text);
        if (text.StartsWith("```", StringComparison.Ordinal) || !segments.Any(segment => segment.IsFormula))
        {
            var markdown = new MarkdownView(0); markdown.SetMarkdown(text); return markdown;
        }
        var panel = new StackPanel { Spacing = 6 };
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment.Text)) continue;
            if (segment.IsFormula)
                panel.Children.Add(new ScrollViewer { HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = new TextBlock { Text = Core.AiAssistant.AssistantText.LatexToPlain(segment.Text), FontFamily = new FontFamily("Cambria Math"),
                        FontSize = 16, IsTextSelectionEnabled = true } });
            else { var markdown = new MarkdownView(0); markdown.SetMarkdown(segment.Text); panel.Children.Add(markdown); }
        }
        return panel;
    }
    private void ApplyTheme()
    {
        _questionHost.Background = ThemeBrushes.Resolve(this, "IcAccentSoftBrush");
        _status.Foreground = _fallback.Foreground = ThemeBrushes.Resolve(this, "IcTextSecondaryBrush");
        _error.Foreground = ThemeBrushes.Resolve(this, "SystemFillColorCriticalBrush");
    }
}
