using InkCanvas.App.Services;
using InkCanvas.Controls.Controls;
using InkCanvas.Controls.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.App.Views;

/// <summary>
/// 通知窗口底部的可选动作按钮。点击后先关闭通知窗口，再由窗口在
/// <c>Closed</c> 回调里执行 <see cref="Invoke"/>，避免动作被模态级置顶窗口阻塞。
/// </summary>
public sealed class NoticeAction
{
    public string Label { get; init; } = "";

    public Func<Task> Invoke { get; init; }

    public bool IsEnabled { get; init; } = true;
}

/// <summary>A dismissible notice with its own layout and native window bounds.</summary>
internal sealed class NoticeWindow : Window
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string Message { get; }
    public Task Completion => _completion.Task;

    public NoticeWindow(string title, string message, int theme)
        : this(title, message, theme, null)
    {
    }

    public NoticeWindow(string title, string message, int theme, IReadOnlyList<NoticeAction> actions)
    {
        Title = title;
        Message = message;
        bool hasActions = actions is { Count: > 0 };
        var body = new Grid { Padding = new Thickness(24), RowSpacing = 20, Background = PopupBrushes.WindowSurface(theme) };
        body.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        body.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 16,
                Foreground = PopupBrushes.Foreground(theme), IsTextSelectionEnabled = true },
        });

        // 动作按钮先记录待执行项并关闭窗口，真正的调用发生在 Closed 里，
        // 这样弹出层不再挡在动作（如打开画板/导出对话框）前面。
        NoticeAction pendingAction = null;
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        if (hasActions)
        {
            foreach (NoticeAction action in actions)
            {
                NoticeAction captured = action;
                var actionButton = new Button
                {
                    Content = captured.Label,
                    MinWidth = 96,
                    MinHeight = 40,
                    IsEnabled = captured.IsEnabled,
                    Foreground = PopupBrushes.Foreground(theme),
                };
                AutomationProperties.SetName(actionButton, captured.Label);
                actionButton.Click += (_, _) => { pendingAction = captured; Close(); };
                buttons.Children.Add(actionButton);
            }
        }

        var close = new Button { Content = "知道了", MinWidth = 112, MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetName(close, "知道了");
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);
        Grid.SetRow(buttons, 1);
        body.Children.Add(buttons);
        body.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Escape) { args.Handled = true; Close(); }
        };
        Content = body;
        WindowChrome.MakeStandard(this, hasActions ? 640 : 520, hasActions ? 300 : 280, resizable: false);
        WindowFrame.Attach(this, body);
        WindowChrome.ApplyTheme(this, theme);
        body.Loaded += (_, _) => close.Focus(FocusState.Programmatic);
        Closed += (_, _) =>
        {
            _completion.TrySetResult();
            if (pendingAction?.Invoke is not null) _ = pendingAction.Invoke();
        };
    }
}