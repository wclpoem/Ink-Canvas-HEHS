using System.Numerics;
using System.Text.Json;
using InkCanvas.AnalyticGeometry;
using InkCanvas.AnalyticGeometry.Algebra;
using InkCanvas.AnalyticGeometry.Construction;
using InkCanvas.Core.Ink;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Svg;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Windows.Storage.Pickers;

namespace InkCanvas.Modules.AnalyticGeometry;

public sealed partial class GeometryWorkbenchWindow
{
    private bool _fileBusy;
    private void CaptureTarget()
    {
        _target?.Controller.ClearExtensionPreview(_previewOwner);
        _target = _host.CaptureTarget();
        var selection = _target.Controller.Selection;
        var source = selection.FirstOrDefault()?.ShapeSource;
        _editing = source is not null && selection.All(stroke => stroke.ShapeSource?.GroupId == source.GroupId)
            && source.Provider is ConstructionShapes.Provider or PlotShapes.Provider
            ? _target.Controller.Document.Strokes.Where(stroke => stroke.ShapeSource?.GroupId == source.GroupId).ToArray() : Array.Empty<InkStroke>();
        if (_editing.Count > 0)
        {
            _protocolStep = int.MaxValue;
            if (source.Provider == ConstructionShapes.Provider) Document.Load(ConstructionExport.Decode(source.Definition));
            else
            {
                var old = JsonSerializer.Deserialize<PlotDefinition>(source.Definition, PlotShapes.JsonOptions) ?? new();
                old.Validate();
                Document.Load(new() { XMin = old.XMin, XMax = old.XMax, YMin = old.YMin, YMax = old.YMax, Axes = old.IncludeAxes });
                Document.Execute($"a={ComputerAlgebra.Number(old.A)};b={ComputerAlgebra.Number(old.B)};c={ComputerAlgebra.Number(old.C)}");
                Document.Execute(old.Kind switch
                {
                    PlotKind.Parametric => $"g=Curve({old.Formula},{old.SecondFormula},t,{ComputerAlgebra.Number(old.TMin)},{ComputerAlgebra.Number(old.TMax)})",
                    PlotKind.Polar => $"g=PolarCurve({old.Formula},{ComputerAlgebra.Number(old.TMin)},{ComputerAlgebra.Number(old.TMax)})",
                    _ => "g=" + old.Formula,
                });
                var attributes = _editing.First(stroke => stroke.ShapeSource?.GroupId == source.GroupId).Attributes;
                Document.Update("g", item => item with { Color = attributes.Color.PackedArgb, LineWidth = Math.Clamp(attributes.BaseWidth,.1,30) });
            }
        }
        _insert.Content = _editing.Count > 0 ? "更新所选构造" : "插入画布";
        QueueCalculation();
    }

    private bool CanInsert() => !_closed && _target?.CanEdit == true && _host.IsCurrent(_target)
        && Evaluation is not null && _renderedRevision == Document.Revision;

    private Matrix3x2 Placement()
    {
        if (_editing.Count > 0)
        {
            var frame = _editing[0].ObjectFrame;
            return Matrix3x2.CreateScale(frame.Bounds.Width / 1000, frame.Bounds.Height / 750) * frame.Transform;
        }
        if (!Matrix3x2.Invert(_target.Controller.ViewTransform, out var inverse)) throw new InvalidOperationException("画布缩放无效。");
        var center = _target.Controller.ViewCenter;
        float scale = Math.Clamp(Math.Min(center.X * 1.5f / 1000, center.Y * 1.4f / 750), .1f, 1);
        return Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(center.X - 500 * scale, center.Y - 375 * scale) * inverse;
    }

    private void PreviewCanvas()
    {
        if (_live.IsChecked != true || !CanInsert()) return;
        Guard(() =>
        {
            _host.EnsureVisible(_target);
            _target.Controller.SetExtensionPreview(_previewOwner, ConstructionShapes.Create(Document.State, Evaluation, Placement(), preview: true));
        });
    }

    public bool InsertShape()
    {
        if (!CanInsert()) return false;
        try
        {
            var strokes = ConstructionShapes.Create(Document.State, Evaluation, Placement(), _editing.FirstOrDefault()?.ShapeSource?.GroupId);
            if (strokes.Count == 0) { SetStatus("当前视图没有可插入的线条或点。"); return false; }
            if (!_host.CommitShape(_target, strokes, _editing)) { SetStatus("目标画布已变化，请重新选择目标。"); return false; }
            _editing = strokes; _insert.Content = "更新所选构造";
            _target.Controller.ClearExtensionPreview(_previewOwner);
            SetStatus("已插入可编辑轮廓与构造数据。选中后打开数学工作区可继续编辑。");
            return true;
        }
        catch (Exception ex) { SetStatus(ex.GetBaseException().Message); return false; }
    }

    private void TargetChanged()
    {
        if (_closed || _host.IsCurrent(_target)) return;
        _target?.Controller.ClearExtensionPreview(_previewOwner);
        _insert.IsEnabled = false;
        SetStatus("画布或页面已切换，请通过菜单重新使用当前画布。");
    }

    private async void RunFileAction(Func<Task> action)
    {
        if (_fileBusy) return;
        _fileBusy = true;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetStatus(ex.GetBaseException().Message); }
        finally { _fileBusy = false; }
    }

    private void InitializePicker(object picker) => WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

    private async Task OpenAsync()
    {
        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".icgeo"); InitializePicker(picker);
        var file = await picker.PickSingleFileAsync();
        if (file is null || _closed) return;
        var state = await ConstructionDocument.OpenAsync(file.Path, _lifetime.Token);
        if (_closed) return;
        StopAnimation(); _protocolStep = int.MaxValue; _editing = Array.Empty<InkStroke>(); _insert.Content = "插入画布"; Document.Load(state);
    }

    private async Task SaveAsync()
    {
        var state = Document.State;
        var picker = new FileSavePicker { SuggestedFileName = "数学构造" };
        picker.FileTypeChoices.Add("Ink Canvas 数学构造", new[] { ".icgeo" }); InitializePicker(picker);
        var file = await picker.PickSaveFileAsync();
        if (file is null || _closed) return;
        await ConstructionDocument.SaveAsync(file.Path, state, _lifetime.Token);
        if (_host.DocumentAi?.Available == true)
            _host.DocumentAi.Saved(new InkCanvas.Host.Contracts.AiDocumentRequest(file.Path, "inkcanvas.analytic-geometry")
            { ExportImages = (directory, token) => ExportAiGeometryAsync(state, directory, token) });
        SetStatus("已保存构造、依赖关系与视图。");
    }

    private static async IAsyncEnumerable<InkCanvas.Host.Contracts.AiDocumentImage> ExportAiGeometryAsync(ConstructionSnapshot state, string directory,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var evaluation = await Task.Run(() => new ConstructionKernel().Evaluate(state), token);
        using var svg = CanvasSvgDocument.LoadFromXml(CanvasDevice.GetSharedDevice(), ConstructionExport.Svg(state, evaluation));
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 1600, 1200, 96);
        using (var drawing = target.CreateDrawingSession())
        { drawing.Clear(Microsoft.UI.Colors.White); drawing.Transform = Matrix3x2.CreateScale(1.6f); drawing.DrawSvg(svg, new Windows.Foundation.Size(1000, 750)); }
        string output = Path.Combine(directory, "geometry.png");
        await target.SaveAsync(output, CanvasBitmapFileFormat.Png).AsTask(token);
        yield return new(output, 1, "geometry", 1600, 1200);
    }

    private async Task ExportAsync(string format)
    {
        await CalculateAsync();
        if (_closed || Evaluation is null || _renderedRevision != Document.Revision) return;
        var state = Document.State;
        var evaluation = Evaluation;
        string extension = format == "TikZ" ? ".tex" : "." + format.ToLowerInvariant();
        var picker = new FileSavePicker { SuggestedFileName = "数学构造" };
        picker.FileTypeChoices.Add(format, new[] { extension }); InitializePicker(picker);
        var file = await picker.PickSaveFileAsync();
        if (file is null || _closed) return;
        switch (format)
        {
            case "SVG": await File.WriteAllTextAsync(file.Path, ConstructionExport.Svg(state, evaluation), _lifetime.Token); break;
            case "STL": await File.WriteAllTextAsync(file.Path, ConstructionExport.Stl(state, evaluation), _lifetime.Token); break;
            case "TikZ": await File.WriteAllTextAsync(file.Path, ConstructionExport.Tikz(state, evaluation), _lifetime.Token); break;
            case "PNG":
                using (var svg = CanvasSvgDocument.LoadFromXml(CanvasDevice.GetSharedDevice(), ConstructionExport.Svg(state, evaluation)))
                using (var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 1600, 1200, 96))
                {
                    using (var ds = target.CreateDrawingSession())
                    { ds.Clear(Microsoft.UI.Colors.White); ds.Transform = Matrix3x2.CreateScale(1.6f); ds.DrawSvg(svg, new Windows.Foundation.Size(1000, 750)); }
                    await target.SaveAsync(file.Path, CanvasBitmapFileFormat.Png);
                }
                break;
            case "PDF": await Task.Run(() => ExportPdf(file.Path, state, evaluation), _lifetime.Token); break;
        }
        SetStatus("已导出 " + format);
    }

    internal static void ExportPdf(string path, ConstructionSnapshot state, ConstructionEvaluation evaluation)
    {
        using var document = new PdfDocument();
        var page = document.AddPage(); page.Width = XUnit.FromPoint(1000); page.Height = XUnit.FromPoint(750);
        using (var graphics = XGraphics.FromPdfPage(page))
        {
            var projection = new GeometryProjection(state, 1000, 750);
            foreach (var item in state.Objects.Where(item => item.Visible))
            {
                if (!evaluation.Values.TryGetValue(item.Label, out var value) || !value.Defined) continue;
                var color = XColor.FromArgb((int)item.Color);
                var fill = new XSolidBrush(XColor.FromArgb((int)(255 * item.Opacity), color.R, color.G, color.B));
                XPoint P(GeoVector point) { var p = projection.Project(point); return new(p.X, p.Y); }
                var fillPath = new XGraphicsPath { FillMode = XFillMode.Winding };
                foreach (var face in value.Faces)
                {
                    var a = P(face.A); var b = P(face.B); var c = P(face.C);
                    if ((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X) < 0) (b, c) = (c, b);
                    fillPath.AddPolygon(new[] { a, b, c });
                }
                if (value.Faces.Count > 0) graphics.DrawPath(fill, fillPath);
                else if (value.Kind == GeometryKind.Region)
                    foreach (var line in projection.Paths(value)) graphics.DrawPolygon(fill, line.Select(p => new XPoint(p.X, p.Y)).ToArray(), XFillMode.Winding);
                foreach (var line in projection.Paths(value)) graphics.DrawLines(new XPen(color, item.LineWidth), line.Select(p => new XPoint(p.X, p.Y)).ToArray());
                foreach (var point in value.Points) { var p = P(point); graphics.DrawEllipse(new XSolidBrush(color), p.X - 4, p.Y - 4, 8, 8); }
            }
        }
        document.Save(path);
    }
}
