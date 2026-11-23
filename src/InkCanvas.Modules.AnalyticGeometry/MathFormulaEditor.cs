using InkCanvas.AnalyticGeometry.Algebra;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VirtualKey = Windows.System.VirtualKey;

namespace InkCanvas.Modules.AnalyticGeometry;

/// <summary>
/// Interactive WinUI 3 RichEditBox MathML WYSIWYG formula editor with fallback preview and
/// two-way synchronization to linear expression syntax.
/// </summary>
internal sealed class MathFormulaEditor : UserControl
{
    private readonly Grid _surface = new();
    private readonly MathFormulaPreview _fallback = new() { MinHeight = 48, MaxHeight = 132, Visibility = Visibility.Collapsed };
    private RichEditBox _editor;
    private bool _nativeUnavailable;
    private bool _syncing;

    public string MathML { get; private set; }
    public bool UsesRichEdit => _editor is not null && !_nativeUnavailable;
    public event Action<string> ExpressionChanged;
    public event Action Submitted;

    public MathFormulaEditor()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        _surface.Children.Add(_fallback);
        Content = _surface;
        AutomationProperties.SetName(this, "MathML 所见即所得公式编辑器");
        TryCreateNativeEditor();
    }

    private void TryCreateNativeEditor()
    {
        if (_nativeUnavailable || _editor is not null) return;
        try
        {
            var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            var editor = new RichEditBox
            {
                FontSize = 21,
                MinHeight = 48,
                MaxHeight = 136,
                Padding = new Thickness(10, 6, 10, 6),
                PlaceholderText = "所见即所得 MathML 公式预览与编辑…",
                IsSpellCheckEnabled = false,
                IsTextPredictionEnabled = false,
                PreventKeyboardDisplayOnProgrammaticFocus = true,
                AcceptsReturn = false,
                BorderThickness = new Thickness(0),
                Background = transparent,
            };
            editor.Resources["TextControlBackground"] = transparent;
            editor.Resources["TextControlBackgroundPointerOver"] = transparent;
            editor.Resources["TextControlBackgroundFocused"] = transparent;
            editor.Resources["TextControlBorderBrush"] = transparent;
            editor.Resources["TextControlBorderBrushPointerOver"] = transparent;
            editor.Resources["TextControlBorderBrushFocused"] = transparent;
            editor.TextDocument.SetMathMode(RichEditMathMode.MathOnly);
            editor.TextDocument.SetMathML("<math xmlns=\"http://www.w3.org/1998/Math/MathML\"><msup><mi>x</mi><mn>2</mn></msup></math>");
            editor.TextDocument.GetMathML(out _);
            editor.TextDocument.SetText(TextSetOptions.None, "");
            editor.TextChanged += OnNativeTextChanged;
            editor.KeyDown += (_, args) =>
            {
                if (args.Key == VirtualKey.Enter) { Submitted?.Invoke(); args.Handled = true; }
            };
            _editor = editor;
            _surface.Children.Add(_editor);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException
            or NotImplementedException or InvalidOperationException or ArgumentException)
        {
            _nativeUnavailable = true;
            _editor = null;
        }
    }

    private void OnNativeTextChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || _editor is null || _nativeUnavailable) return;
        try
        {
            _editor.TextDocument.GetMathML(out string markup);
            if (string.IsNullOrWhiteSpace(markup))
            {
                MathML = null;
                ExpressionChanged?.Invoke("");
                return;
            }
            string expression = MathMarkup.ToExpression(markup);
            MathML = markup;
            ExpressionChanged?.Invoke(expression);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            // User may be mid-typing an incomplete math structure; keep waiting without throwing.
        }
    }

    public bool SetExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            Clear();
            return false;
        }
        try
        {
            string markup = MathMarkup.ToMathML(expression);
            SetMathML(markup);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            MathML = null;
            return false;
        }
    }

    public void SetMathML(string markup)
    {
        MathMarkup.ToExpression(markup);
        if (!_nativeUnavailable)
        {
            TryCreateNativeEditor();
            if (_editor is not null)
            {
                _syncing = true;
                try
                {
                    _editor.TextDocument.SetMathML(markup);
                    _editor.TextDocument.GetMathML(out string nativeMarkup);
                    MathML = string.IsNullOrWhiteSpace(nativeMarkup) ? markup : nativeMarkup;
                    _editor.Visibility = Visibility.Visible;
                    _fallback.Visibility = Visibility.Collapsed;
                    return;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException
                    or NotImplementedException or InvalidOperationException)
                {
                    _nativeUnavailable = true;
                    _editor.Visibility = Visibility.Collapsed;
                }
                finally { _syncing = false; }
            }
        }
        _fallback.SetMathML(markup);
        _fallback.Visibility = Visibility.Visible;
        MathML = markup;
    }

    public void Clear()
    {
        MathML = null;
        _fallback.Visibility = Visibility.Collapsed;
        if (_editor is not null && !_nativeUnavailable)
        {
            _syncing = true;
            try { _editor.TextDocument.SetText(TextSetOptions.None, ""); }
            catch (Exception) { }
            finally { _syncing = false; }
        }
    }
}
