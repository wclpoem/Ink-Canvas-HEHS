using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Selection;

namespace InkCanvas.Ink;

/// <summary>Bridge for image/video objects without coupling the ink controller to board UI.</summary>
public interface ISelectionElement
{
    Guid Id { get; }
    long? LayerOrder { get; set; }
    Rect Bounds { get; }
    SelectionFrame Frame { get; }
    bool IsPresent { get; }
    object Capture();
    void Restore(object snapshot);
    void Transform(Matrix3x2 delta);
    void TransformFrom(object snapshot, Matrix3x2 delta) { Restore(snapshot); Transform(delta); }
    ISelectionElement Duplicate();
    void SetPresent(bool present);
}

/// <summary>An embedded raster or vector image, with geometry supplied by its selection frame.</summary>
public interface IImageSelectionElement : ISelectionElement
{
    byte[] ImageData { get; }
    string ImageContentType { get; }
    IReadOnlyList<IReadOnlyList<Core.AiAssistant.ShapeAnnotationPoint>> ImageAnnotations => Array.Empty<IReadOnlyList<Core.AiAssistant.ShapeAnnotationPoint>>();
    float Opacity { get; set; }
}
