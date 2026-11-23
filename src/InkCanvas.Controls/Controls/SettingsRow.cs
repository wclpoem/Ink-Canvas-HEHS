using InkCanvas.Controls.Theming;
using Microsoft.UI.Xaml;
using InkCanvas.Controls.Lifecycle;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.Controls.Controls;

/// <summary>A settings label and editor that share a row when space allows.</summary>
public sealed class SettingsRow : Grid
{
    private readonly FrameworkElement _editor;
    private readonly ColumnDefinition _editorColumn;
    private readonly double _editorWidth;
    private readonly TextBlock _description;
    public string Title { get; }
    public FrameworkElement Editor => _editor;
    public string Description
    {
        get => _description.Text;
        set { _description.Text = value ?? ""; _description.Visibility = string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible; }
    }
    public bool IsStacked { get; private set; }

    public SettingsRow(string title, FrameworkElement editor, int theme, string description = null, double editorWidth = 0)
    {
        Title = title;
        _editor = editor;
        _editorWidth = editorWidth;
        MinHeight = 40;
        ColumnSpacing = 24;
        ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _editorColumn = new() { Width = editorWidth > 0 ? new GridLength(editorWidth) : GridLength.Auto };
        ColumnDefinitions.Add(_editorColumn);
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Text = title, FontSize = 14, TextWrapping = TextWrapping.Wrap,
            Foreground = PopupBrushes.Foreground(theme) };
        AutomationProperties.SetName(label, title);
        text.Children.Add(label);
        _description = new TextBlock { FontSize = 12, LineHeight = 18,
                TextWrapping = TextWrapping.Wrap, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(theme == 2
                    ? Windows.UI.Color.FromArgb(168, 255, 255, 255) : Windows.UI.Color.FromArgb(158, 0, 0, 0)) };
        Description = description;
        text.Children.Add(_description);
        editor.VerticalAlignment = VerticalAlignment.Center;
        editor.HorizontalAlignment = editorWidth > 0 ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        SetColumn(editor, 1);
        Children.Add(text);
        Children.Add(editor);
        DeferredLayout.Observe(this, UpdateLayoutMode);
    }

    private void UpdateLayoutMode()
    {
        bool stacked = ActualWidth < Math.Max(380, _editorWidth + 240);
        if (stacked == IsStacked) return;
        IsStacked = stacked;
        RowSpacing = stacked ? 10 : 0;
        ColumnSpacing = stacked ? 0 : 24;
        _editorColumn.Width = stacked ? new GridLength(0)
            : _editorWidth > 0 ? new GridLength(_editorWidth) : GridLength.Auto;
        SetColumn(_editor, stacked ? 0 : 1);
        SetRow(_editor, stacked ? 1 : 0);
        _editor.HorizontalAlignment = stacked && _editor is ToggleSwitch ? HorizontalAlignment.Left
            : _editorWidth > 0 || stacked ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
    }
}
