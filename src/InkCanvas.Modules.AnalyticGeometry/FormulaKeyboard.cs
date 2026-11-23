using InkCanvas.Controls.Controls;
using Microsoft.UI.Xaml;
using InkCanvas.Controls.Lifecycle;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace InkCanvas.Modules.AnalyticGeometry;

/// <summary>Touch-first formula palette with grouped symbols, functions, templates and Greek letters.</summary>
public sealed class FormulaKeyboard : UserControl
{
    private static readonly string[] WideBasic =
    {
        "a", "b", "c", "x", "y", "t", "(", "7", "8", "9",
        "平方根", "分数", "上标", "下标", "sin", "cos", ")", "4", "5", "6",
        "π", "x²", "x³", "绝对值", "×", "÷", ",", "1", "2", "3",
        "←", "→", "⇥", "⌫", "+", "−", "=", "0", ".", "e",
    };
    private static readonly string[][] Pages =
    {
        new[] { "x", "y", "t", "(", "7", "8", "9", "a", "b", "c", ")", "4", "5", "6",
            "分数", "平方根", "上标", "÷", "1", "2", "3", "下标", "x²", "x³", "×", "0", ".", "=",
            "←", "→", "⇥", "⌫", "+", "−", "," },
        new[] { "sin", "cos", "tan", "asin", "acos", "atan", "sinh", "cosh", "tanh", "sqrt", "abs", "exp", "ln", "log",
            "log10", "floor", "ceil", "round", "min", "max", "pow", "atan2", "x", "y", "t", "a", "b", "c", "←", "→", "⌫" },
        new[] { "分数", "平方根", "上标", "下标", "括号", "倒数", "绝对值", "平方", "立方", "()", "[]", "{}", "|x|",
            "x²", "x³", "x^n", "1/x", "√", "^", "_", "(", ")", "[", "]", "←", "→", "⌫" },
        new[] { "π", "θ", "α", "β", "γ", "δ", "ε", "λ", "μ", "σ", "φ", "ω", "∞", "≈", "≠", "≤", "≥", "∠", "°", "e", "i", "x", "y", "t", "n", "k", "⇥", "←", "→", "⌫" },
        new[] { "Simplify()", "Expand()", "Factor()", "Numeric()", "Solve()", "Substitute()", "Derivative()", "Integral()", "Limit()", "Determinant()", "Inverse()", "Transpose()", "Rank()", "Real()", "Imaginary()", "矩阵", "分段", "x", "y", "z", "i", "=", ",", "(", ")", "⇥", "←", "→", "⌫" },
        new[] { "Point()", "Line()", "Segment()", "Circle()", "Polygon()", "Intersect()", "Midpoint()", "Vector()", "Parallel()", "Perpendicular()", "Rotate()", "Translate()", "Reflect()", "Dilate()", "Distance()", "Area()", "Curve()", "Surface()", "Plane()", "Sphere()", "Prism()", "Pyramid()", "Net()", "Revolve()", "x", "y", "z", "t", "a", "b", "⇥", "←", "→", "⌫" },
    };

    public event Action<string> KeyPressed;

    public FormulaKeyboard()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        var panel = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
        string[] pageNames = { "基本", "函数", "结构", "符号", "代数", "几何" };
        var selector = new SegmentedSelector(pageNames) { HorizontalAlignment = HorizontalAlignment.Stretch };
        for (int index = 0; index < pageNames.Length; index++)
            selector.SetOptionContent(index, new TextBlock { Text = pageNames[index], VerticalAlignment = VerticalAlignment.Center }, 44);
        var host = new ContentControl { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        int renderedColumns = 0;
        void ShowPage()
        {
            int columns = ActualWidth >= 720 ? 10 : 7;
            renderedColumns = columns;
            var keys = selector.SelectedIndex == 0 && columns == 10 ? WideBasic : Pages[Math.Clamp(selector.SelectedIndex, 0, Pages.Length - 1)];
            var grid = new Grid { ColumnSpacing = 4, RowSpacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
            for (int column = 0; column < columns; column++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int rows = (keys.Length + columns - 1) / columns;
            for (int row = 0; row < rows; row++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
            for (int index = 0; index < keys.Length; index++)
            {
                string key = keys[index];
                var button = new Button
                {
                    Content = KeyContent(key),
                    MinWidth = 0,
                    MinHeight = 44,
                    Padding = new Thickness(2),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    AllowFocusOnInteraction = false,
                    FontSize = key.Length > 5 ? 12 : key.Length > 2 ? 14 : 18,
                };
                AutomationProperties.SetName(button, "公式键 " + key);
                ToolTipService.SetToolTip(button, Tip(key));
                button.Click += (_, _) => KeyPressed?.Invoke(key);
                Grid.SetColumn(button, index % columns);
                Grid.SetRow(button, index / columns);
                grid.Children.Add(button);
            }
            host.Content = grid;
        }
        selector.SelectionChanged += ShowPage;
        DeferredLayout.Observe(this, () => { if (renderedColumns != (ActualWidth >= 720 ? 10 : 7)) ShowPage(); });
        panel.Children.Add(selector);
        panel.Children.Add(host);
        Content = panel;
        selector.SelectedIndex = 0;
    }

    public void SendKey(string key) => KeyPressed?.Invoke(key);

    private static object KeyContent(string key)
    {
        string label = key switch
        {
            "平方根" => "√x", "上标" or "x^n" => "xⁿ", "下标" => "xₙ", "绝对值" => "|x|",
            "括号" => "(x)", "倒数" => "1/x", "平方" => "x²", "立方" => "x³", "分段" => "{…", "矩阵" => "[▦]",
            "Simplify()" => "化简", "Expand()" => "展开", "Factor()" => "因式分解", "Numeric()" => "近似值",
            "Solve()" => "求解", "Substitute()" => "代入", "Derivative()" => "d/dx", "Integral()" => "∫",
            "Limit()" => "lim", "Determinant()" => "det", "Inverse()" => "A⁻¹", "Transpose()" => "Aᵀ",
            "Rank()" => "rank", "Real()" => "Re", "Imaginary()" => "Im", _ => key.Replace("()", ""),
        };
        if (key != "分数") return label;
        var fraction = new Grid { Width = 22, RowSpacing = 1 };
        fraction.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        fraction.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
        fraction.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var numerator = new TextBlock { Text = "a", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center };
        fraction.Children.Add(numerator);
        var bar = new Border();
        bar.SetBinding(Border.BackgroundProperty, new Binding { Source = numerator, Path = new PropertyPath(nameof(TextBlock.Foreground)) });
        Grid.SetRow(bar, 1); fraction.Children.Add(bar);
        var denominator = new TextBlock { Text = "b", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center };
        Grid.SetRow(denominator, 2); fraction.Children.Add(denominator);
        return fraction;
    }

    private static string Tip(string key) => key switch
    {
        "分数" => "在光标处插入分数模板",
        "平方根" or "√" => "插入平方根模板",
        "上标" or "^" or "x^n" => "插入上标模板",
        "下标" or "_" => "插入下标模板",
        "⌫" => "删除前一个字符",
        "⇥" => "移到下一个公式占位符",
        "←" or "→" => "移动插入光标",
        _ => key,
    };
}
