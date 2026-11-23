using InkCanvas.AnalyticGeometry.Algebra;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.Modules.AnalyticGeometry;

/// <summary>RichEdit MathML WYSIWYG rendering with a native XAML fallback for older Windows installations.</summary>
internal sealed class MathFormulaView : UserControl
{
    private readonly Grid _surface = new();
    private readonly MathFormulaPreview _fallback = new();
    private RichEditBox _native;
    private bool _nativeUnavailable;
    private string _expression;
    private readonly bool _compact;

    public string MathML { get; private set; }
    public bool UsesRichEdit => _native is not null && !_nativeUnavailable;

    public MathFormulaView(bool compact = false)
    {
        _compact = compact;
        Content = _surface;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        if (compact)
        {
            _fallback.MinHeight = 28;
            _fallback.MaxHeight = 76;
        }
        _surface.Children.Add(_fallback);
        AutomationProperties.SetName(this, "公式排版");
    }

    public bool SetExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            MathML = null;
            _expression = null;
            return false;
        }
        if (_expression == expression && MathML is not null) return true;
        try
        {
            string markup = MathMarkup.ToMathML(expression);
            SetMathML(markup);
            _expression = expression;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            MathML = null;
            _expression = null;
            return false;
        }
    }

    public void SetMathML(string markup)
    {
        // Validate markup before touching RichEdit, whose SetMathML clears invalid content.
        MathMarkup.ToExpression(markup);
        if (!_nativeUnavailable)
        {
            try
            {
                if (_native is null)
                {
                    var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    _native = new RichEditBox
                    {
                        FontSize = _compact ? 17 : 22,
                        MinHeight = _compact ? 30 : 52,
                        MaxHeight = _compact ? 84 : 144,
                        Padding = _compact ? new Thickness(2, 2, 2, 2) : new Thickness(8, 6, 8, 6),
                        IsSpellCheckEnabled = false,
                        IsTextPredictionEnabled = false,
                        PreventKeyboardDisplayOnProgrammaticFocus = true,
                        AcceptsReturn = false,
                        IsTabStop = false,
                        BorderThickness = new Thickness(0),
                        Background = transparent,
                        IsHitTestVisible = false,
                    };
                    _native.Resources["TextControlBackground"] = transparent;
                    _native.Resources["TextControlBackgroundPointerOver"] = transparent;
                    _native.Resources["TextControlBackgroundFocused"] = transparent;
                    _native.Resources["TextControlBackgroundDisabled"] = transparent;
                    _native.Resources["TextControlBorderBrush"] = transparent;
                    _native.Resources["TextControlBorderBrushPointerOver"] = transparent;
                    _native.Resources["TextControlBorderBrushFocused"] = transparent;
                    _native.TextDocument.SetMathMode(RichEditMathMode.MathOnly);
                    _surface.Children.Add(_native);
                }
                _native.IsReadOnly = false;
                _native.TextDocument.SetMathML(markup);
                _native.TextDocument.GetMathML(out string nativeMarkup);
                _native.IsReadOnly = true;
                MathML = string.IsNullOrWhiteSpace(nativeMarkup) ? markup : nativeMarkup;
                _fallback.Visibility = Visibility.Collapsed;
                _native.Visibility = Visibility.Visible;
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException
                or NotImplementedException or InvalidOperationException)
            {
                _nativeUnavailable = true;
                if (_native is not null) _native.Visibility = Visibility.Collapsed;
            }
        }
        _fallback.SetMathML(markup);
        _fallback.Visibility = Visibility.Visible;
        MathML = markup;
    }
}
