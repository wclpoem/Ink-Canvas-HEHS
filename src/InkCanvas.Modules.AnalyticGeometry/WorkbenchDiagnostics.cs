using System.Diagnostics;
using System.Text.Json;
using InkCanvas.AnalyticGeometry.Algebra;
using InkCanvas.AnalyticGeometry.Construction;
using InkCanvas.Features.Persistence;
using InkCanvas.Host.Contracts;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Svg;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace InkCanvas.Modules.AnalyticGeometry;

public static class WorkbenchDiagnostics
{
    public static async Task RunAsync(ICanvasToolsHost host, string directory)
    {
        var checks = new List<object>();
        GeometryWorkbenchWindow window = null;
        try
        {
            var target = host.CaptureTarget();
            var legacy = target.Controller.Selection.FirstOrDefault(stroke => stroke.ShapeSource?.Provider == PlotShapes.Provider);
            if (legacy is not null)
            {
                var originalFrame = legacy.ObjectFrame;
                window = new(host); window.Activate(); await window.CalculateAsync();
                Check("legacy formula opens in native workbench", window.Document.State.Objects.Any(item => item.Label == "g") && window.Evaluation.Values["g"].Defined);
                bool migrated = window.InsertShape();
                var converted = target.Controller.Selection.FirstOrDefault(stroke => stroke.ShapeSource?.Provider == ConstructionShapes.Provider);
                Check("legacy migration retains style and object placement", migrated && converted is not null
                    && converted.Attributes.Color == legacy.Attributes.Color && Math.Abs(converted.Attributes.BaseWidth - legacy.Attributes.BaseWidth) < .001
                    && System.Numerics.Vector2.Distance(originalFrame.Corners(System.Numerics.Matrix3x2.Identity)[0],converted.ObjectFrame.Corners(System.Numerics.Matrix3x2.Identity)[0]) < .01
                    && System.Numerics.Vector2.Distance(originalFrame.Corners(System.Numerics.Matrix3x2.Identity)[2],converted.ObjectFrame.Corners(System.Numerics.Matrix3x2.Identity)[2]) < .01, window.StatusText);
                window.Close(); window = null;
            }
            target.Controller.ClearSelection();
            window = new(host); window.Activate(); await Task.Delay(400);
            Check("native workbench uses shared WinUI title bar", window.ExtendsContentIntoTitleBar && window.Content is Controls.Controls.WindowFrame);
            window.FormulaInput.Text = "h(x)=";
            window.FormulaInput.Focus(FocusState.Programmatic);
            window.FormulaInput.SelectionStart = window.FormulaInput.Text.Length;
            window.Keyboard.SendKey("分数");
            Check("workbench fraction template selects its numerator", window.FormulaInput.Text == "h(x)=(□)/(□)" && window.FormulaInput.SelectedText == "□");
            window.Keyboard.SendKey("x"); window.Keyboard.SendKey("x²"); window.Keyboard.SendKey("⇥"); window.Keyboard.SendKey("2");
            await Task.Delay(200);
            Check("workbench keyboard fills and navigates structural placeholders", window.FormulaInput.Text == "h(x)=(x^2)/(2)");
            Check("main plugin entry displays MathML before creating a shape", window.FormulaMathML?.Contains("mfrac", StringComparison.Ordinal) == true,
                new { nativeRichEdit = window.FormulaUsesRichEdit });
            window.Execute(window.FormulaInput.Text); await window.CalculateAsync();
            Check("template expression uses the normal construction and algebra pipeline", window.Evaluation.Values["h"].Defined);
            var keyboardPanel = Descendants(window.Content).OfType<Expander>().Single(item => AutomationProperties.GetName(item) == "数学键盘与手写输入");
            keyboardPanel.IsExpanded = true;
            (window.Content as FrameworkElement)?.UpdateLayout();
            await Task.Delay(150);
            var keys = Descendants(window.Keyboard).OfType<Button>().Where(button => AutomationProperties.GetName(button).StartsWith("公式键 ")).ToArray();
            Check("formula keyboard uses touch-sized keys across its input width", keys.Length > 20 && keys.All(button => button.ActualHeight >= 44 && button.ActualWidth >= 40));
            await ScreenshotAsync(window, Path.Combine(directory, "math-keyboard.png"));
            window.FormulaInput.Text = "[[1,2],[3/2,-1/2]]";
            await Task.Delay(150);
            Check("workbench displays matrix and fraction structure", window.FormulaMathML?.Contains("mtable", StringComparison.Ordinal) == true);
            var surface = (FrameworkElement)window.Content;
            surface.RequestedTheme = ElementTheme.Dark;
            await Task.Delay(150);
            await ScreenshotAsync(window, Path.Combine(directory, "math-dark.png"));
            surface.RequestedTheme = ElementTheme.Light;
            await Task.Delay(100);
            await ScreenshotAsync(window, Path.Combine(directory, "math-matrix.png"));
            window.FormulaInput.Text = "f(x)=";
            await Task.Delay(150);
            Check("incomplete input hides the previous mathematical preview", Descendants(window.Content).OfType<MathFormulaView>()
                .Where(view => AutomationProperties.GetName(view) == "输入公式排版")
                .All(view => view.Visibility == Visibility.Collapsed));
            window.FormulaInput.Text = "";
            keyboardPanel.IsExpanded = false;
            foreach (var example in GeometryWorkbenchWindow.ExampleCommands)
            {
                try
                {
                    if (example.Group == "CAS")
                    {
                        var result = await Task.Run(() => ComputerAlgebra.Calculate(example.Input));
                        Check("CAS example " + example.Name, result.Exact.Length > 0, result.Exact);
                    }
                    else
                    {
                        window.Document.Load(new()); window.Execute(example.Input); await window.CalculateAsync();
                        var errors = window.Evaluation?.Values.Where(pair => !pair.Value.Defined).Select(pair => pair.Key + " " + pair.Value.Error).ToArray();
                        Check("construction example " + example.Name, errors?.Length == 0, errors);
                    }
                }
                catch (Exception ex) { Check("example " + example.Name, false, ex.ToString()); }
            }
            window.Document.Load(new());
            window.Execute("a=2;show=Checkbox(true);input=InputBox(a);choice=Dropdown({1,2,3},2);next=Button(\"下一步\")");
            window.Document.Update("next", item => item with { OnClick = "js: ggbApplet.setValue('a',ggbApplet.getValue('a')+1);" });
            await window.CalculateAsync(); await Task.Delay(100);
            var nextButton = Descendants(window.Content).OfType<Button>().FirstOrDefault(button => button.Content?.ToString() == "下一步");
            Check("native graph hosts interactive controls", nextButton is not null && Descendants(window.Content).OfType<TextBox>().Any(box => box.Header?.ToString() == "a"));
            if (nextButton is not null)
            {
                ((IInvokeProvider)new ButtonAutomationPeer(nextButton).GetPattern(PatternInterface.Invoke)).Invoke();
                for (int i = 0; i < 40 && window.Evaluation?.Values.GetValueOrDefault("a")?.Number != 3; i++) await Task.Delay(50);
                Check("native button runs local JavaScript and refreshes dependencies", window.Evaluation?.Values.GetValueOrDefault("a")?.Number == 3);
            }
            window.Document.Load(new());
            var input = Descendants(window.Content).OfType<TextBox>().First(box => box.PlaceholderText.StartsWith("输入表达式"));
            input.Text = "a=1;A=(-3,0);B=(3,2);M=Midpoint(A,B);s=Segment(A,B);f(x)=a*x^2";
            var create = Descendants(window.Content).OfType<Button>().First(button => button.Content?.ToString() == "创建 / 更新");
            ((IInvokeProvider)new ButtonAutomationPeer(create).GetPattern(PatternInterface.Invoke)).Invoke();
            await window.CalculateAsync();
            Check("native command input creates linked objects", window.Evaluation?.Values["M"].Origin.X == 0);
            input.Text = "x"; input.SelectionStart = 1;
            window.Keyboard.SendKey("x²");
            Check("native math keyboard preserves caret insertion", input.Text == "x^2");
            input.Text = "Integral(f,x,0,3)";
            var tabs = Descendants(window.Content).OfType<Controls.Controls.SegmentedSelector>()
                .First(selector => AutomationProperties.GetName(selector) == "工作区视图切换");
            tabs.SelectedIndex = 2;
            await window.CalculateCasAsync();
            await Task.Delay(80);
            Check("CAS input resolves geometry function definitions", window.LastCasResult?.Exact == "9"
                && Descendants(window.Content).OfType<TextBox>().Any(box => box.IsReadOnly && box.Text == "9"), window.LastCasResult);
            tabs.SelectedIndex = 0;
            window.Document.BeginGesture(); window.Execute("A=(-5,2)"); window.Execute("A=(-7,4)"); window.Document.EndGesture();
            await window.CalculateAsync();
            Check("point edits update dependent geometry", window.Evaluation.Values["M"].Origin.X == -2 && window.Evaluation.Values["M"].Origin.Y == 3);
            window.Document.Undo(); await window.CalculateAsync();
            Check("drag transaction undoes in one step", window.Evaluation.Values["A"].Origin.X == -3);
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 40; i++) window.Execute("a=" + ComputerAlgebra.Number(i / 10d));
            await window.CalculateAsync();
            Check("rapid parameters coalesce to latest result", window.Evaluation.Values["a"].Number == 3.9 && watch.ElapsedMilliseconds < 3000, new { ms = watch.ElapsedMilliseconds });
            var live = Descendants(window.Content).OfType<CheckBox>().First(box => box.Content?.ToString() == "实时预览到画布");
            live.IsChecked = true;
            Check("native workbench previews without committing", target.Controller.ExtensionPreview.Count > 0);
            int count = target.Controller.Document.Count;
            Check("workbench inserts grouped editable contours", window.InsertShape(), window.StatusText);
            var group = target.Controller.Selection.FirstOrDefault()?.ShapeSource;
            Check("construction metadata round trips", group?.Provider == ConstructionShapes.Provider
                && ConstructionExport.Decode(group.Definition).Objects.Any(item => item.Label == "M"));
            int inserted = target.Controller.Document.Count;
            target.Controller.Undo(); Check("construction insertion is a single undo step", target.Controller.Document.Count == count);
            target.Controller.Redo(); Check("construction insertion is a single redo step", target.Controller.Document.Count == inserted);
            string archive = Path.Combine(directory, "construction.icink"); InkFileStore.Save(archive, target.Controller.Document.Strokes);
            Check("canvas archive retains full construction", InkFileStore.Load(archive).Any(stroke => stroke.ShapeSource?.Provider == ConstructionShapes.Provider));
            string workspace = Path.Combine(directory, "construction.icgeo");
            await ConstructionDocument.SaveAsync(workspace, window.Document.State);
            Check("workspace retains definitions", (await ConstructionDocument.OpenAsync(workspace)).Objects.Count == window.Document.State.Objects.Count);
            var svg = ConstructionExport.Svg(window.Document.State, window.Evaluation);
            await File.WriteAllTextAsync(Path.Combine(directory, "construction.svg"), svg);
            using (var document = CanvasSvgDocument.LoadFromXml(CanvasDevice.GetSharedDevice(), svg))
            using (var bitmap = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 1000, 750, 96))
            {
                using (var ds = bitmap.CreateDrawingSession()) { ds.Clear(Microsoft.UI.Colors.White); ds.DrawSvg(document, new Windows.Foundation.Size(1000, 750)); }
                await bitmap.SaveAsync(Path.Combine(directory, "construction.png"), CanvasBitmapFileFormat.Png);
                Check("native SVG and PNG render nonempty geometry", bitmap.GetPixelBytes().Count(b => b < 220) > 1000);
            }
            GeometryWorkbenchWindow.ExportPdf(Path.Combine(directory, "construction.pdf"), window.Document.State, window.Evaluation);
            Check("native vector PDF export", new FileInfo(Path.Combine(directory, "construction.pdf")).Length > 300);
            await ScreenshotAsync(window, Path.Combine(directory, "workbench-2d.png"));
            window.Document.Load(new() { ThreeDimensional = true });
            window.Execute("g=Cube((-2,-2,-2),4);p=Plane((0,0,0),Vector((1,1,1)));c=Intersect(p,g)");
            await window.CalculateAsync(); await Task.Delay(180);
            Check("3D view renders solid and section", window.Evaluation.Values["c"].Paths.Count > 0);
            Check("3D mode button follows loaded construction", Descendants(window.Content).OfType<Controls.Controls.SegmentedSelector>().Any(selector => selector.SelectedIndex == 1));
            await ScreenshotAsync(window, Path.Combine(directory, "workbench-3d.png"));
            window.Close(); window = null;
            Check("closing workbench releases canvas preview", target.Controller.ExtensionPreview.Count == 0);
            window = new(host); window.Activate(); await window.CalculateAsync();
            Check("selected construction reopens with dependencies", window.Document.State.Objects.Any(item => item.Label == "M"));
        }
        catch (Exception ex) { Check("workbench runtime exception", false, ex.ToString()); }
        finally
        {
            window?.Close();
            await File.WriteAllTextAsync(Path.Combine(directory, "workbench.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        }
        void Check(string check, bool pass, object detail = null) => checks.Add(new { check, pass, detail });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        if (root is null) yield break;
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static async Task ScreenshotAsync(Window window, string path)
    {
        await Task.Delay(120);
        var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(window.Content as FrameworkElement);
        var pixels = await bitmap.GetPixelsAsync(); byte[] bytes = new byte[pixels.Length]; DataReader.FromBuffer(pixels).ReadBytes(bytes);
        using var memory = new InMemoryRandomAccessStream(); var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memory);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
        await encoder.FlushAsync(); memory.Seek(0);
        using var output = File.Create(path); await memory.AsStreamForRead().CopyToAsync(output);
    }
}
