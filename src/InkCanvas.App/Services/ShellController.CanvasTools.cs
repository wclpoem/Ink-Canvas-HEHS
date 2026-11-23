using InkCanvas.Core.History;
using InkCanvas.Core.Ink;
using InkCanvas.Host.Contracts;
using InkCanvas.Ink;
using Microsoft.UI.Xaml;

namespace InkCanvas.App.Services;

public sealed partial class ShellController
{
    internal ICanvasToolsHost CanvasToolsTestHost => new CanvasToolsHost(this);
    private event Action CanvasToolTargetChanged;
    private sealed class CanvasToolsHost(ShellController shell) : ICanvasToolsHost
    {
        public IAiDocumentBridge DocumentAi => shell._documentAi;
        public CanvasToolTarget CaptureTarget() => new(shell.ActiveController, shell.DocumentActive ? "document" : shell._board is null ? "screen" : "board", !shell.DocumentActive || shell._documentWorkspace.CanEdit);
        public bool IsCurrent(CanvasToolTarget target) => !shell._disposed && !shell._exiting && ReferenceEquals(target.Controller, shell.ActiveController);
        public event Action TargetChanged { add => shell.CanvasToolTargetChanged += value; remove => shell.CanvasToolTargetChanged -= value; }
        public void ConfigureWindow(Window window, int width = 1180, int height = 880) => new DocumentHost(shell).ConfigureWindow(window, width, height);
        public void EnsureVisible(CanvasToolTarget target)
        {
            if (!IsCurrent(target) || target.Kind != "screen") return;
            shell.Overlay.SetInteractionMode(clickThrough: false, inkVisible: true);
            shell.OnControllerToolChanged();
        }
        public bool CommitShape(CanvasToolTarget target, IReadOnlyList<InkStroke> strokes, IReadOnlyList<InkStroke> replaced = null)
        {
            if (!IsCurrent(target) || !target.CanEdit || strokes.Count == 0 || target.Controller.HasActiveInteraction || target.Controller.Playback is not null) return false;
            var controller = target.Controller;
            var old = (replaced ?? Array.Empty<InkStroke>()).ToArray();
            if (old.Any(stroke => !controller.Document.Strokes.Contains(stroke))) return false;
            var oldSet = old.ToHashSet();
            var indices = controller.Document.Strokes.Select((stroke, index) => (Index: index, Stroke: stroke)).Where(item => oldSet.Contains(item.Stroke)).ToArray();
            long layer = old.Length > 0 ? old.Min(stroke => stroke.LayerOrder ?? 0) : controller.NextLayerOrder();
            foreach (var stroke in strokes) stroke.LayerOrder = layer;
            Apply();
            controller.History.Push(new DelegateAction(old.Length > 0 ? "修改解析几何" : "插入解析几何", Apply, () =>
            {
                controller.ClearSelection(); controller.Document.RemoveAll(strokes); controller.Document.InsertRange(indices);
            }));
            return true;
            void Apply()
            {
                controller.ClearSelection(); controller.Document.RemoveAll(old); controller.Document.AddRange(strokes);
                controller.Tool = InkTool.Select; controller.Selection.AddRange(strokes); controller.NotifyElementsChanged();
            }
        }
    }
}
