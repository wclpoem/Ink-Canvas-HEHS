using InkCanvas.Core.History;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Input;
using InkCanvas.Core.Recognition;
using InkCanvas.Core.Selection;
using InkCanvas.Core.Settings;
using InkCanvas.Core.Shapes;
using InkCanvas.Ink.Manipulation;
using InkCanvas.Platform.Input;
using InkCanvas.Platform.Windows;
using System.Numerics;

namespace InkCanvas.Ink;

public enum InkTool { Pen, Highlighter, Eraser, Select, Shape, Pan }
public enum InkEraserMode { Area, WholeStroke, Lasso }

/// <summary>
/// The single entry point the UI uses for ink: routes input, owns the document and history,
/// and raises <see cref="Invalidated"/> when a redraw is needed.
/// <para>
/// Commit is deliberately <b>atomic</b>: the stroke is completed, moved from the live layer
/// into the document, and recorded in history before a single invalidation is raised. There
/// is therefore no frame in which the stroke is missing from both layers or present in both,
/// and no frame in which its geometry is recomputed.
/// </para>
/// </summary>
public sealed partial class InkController : IDisposable
{
    public InkController() => Document.Changed += OnPlaybackDocumentChanged;
    public InkPlayback Playback { get; private set; }
    public event Action PlaybackChanged;
    public bool BeginPlayback()
    {
        EndPlayback();
        FinishDotAnimations();
        HideEraserPreview();
        if (_live.Count > 0 || IsLassoErasing || _erasing) return false;
        var source = Document.Strokes.Where(s => Selection.Count == 0 || Selection.Contains(s)).ToList();
        if (source.Count == 0) return false;
        Playback = new InkPlayback(source);
        PlaybackChanged?.Invoke(); Invalidated?.Invoke(); return true;
    }
    public void SeekPlayback(float elapsedMs)
    {
        if (Playback is null) return;
        Playback.Seek(elapsedMs); Invalidated?.Invoke();
    }
    public void EndPlayback()
    {
        if (Playback is null) return;
        Playback = null; PlaybackChanged?.Invoke(); Invalidated?.Invoke();
    }
    private void OnPlaybackDocumentChanged()
    {
        RefreshShapeConnections();
        _drawingOrderDirty = true;
        _drawingObjects = Array.Empty<DrawingObject>();
        EndPlayback();
        if (_dotAnimations.RemoveAll(animation => !Document.Strokes.Contains(animation.Source)
            || animation.SourceVersion != animation.Source.GeometryVersion) == 0) return;
        DotAnimationVersion++;
        DotAnimationChanged?.Invoke();
    }

    private readonly List<InkDotAnimation> _dotAnimations = new();
    public IReadOnlyList<InkDotAnimation> DotAnimations => _dotAnimations;
    public long DotAnimationVersion { get; private set; }
    public event Action DotAnimationChanged;
    public bool IsAnimatingDot(InkStroke stroke)
    {
        foreach (var animation in _dotAnimations)
            if (ReferenceEquals(animation.Source, stroke)) return true;
        return false;
    }

    public void AdvanceDotAnimations(float milliseconds)
    {
        if (_dotAnimations.Count == 0 || !float.IsFinite(milliseconds) || milliseconds <= 0) return;
        for (int index = _dotAnimations.Count - 1; index >= 0; index--)
        {
            var animation = _dotAnimations[index];
            if (animation.Advance(milliseconds))
            {
                _dotAnimations.RemoveAt(index);
                DotAnimationVersion++;
            }
        }
        DotAnimationChanged?.Invoke();
    }

    public void FinishDotAnimations()
    {
        if (_dotAnimations.Count == 0) return;
        _dotAnimations.Clear();
        DotAnimationVersion++;
        DotAnimationChanged?.Invoke();
    }
    private readonly Dictionary<uint, InkStroke> _live = new();
    private readonly Dictionary<uint, uint> _strokeStartTimes = new();
    private readonly Dictionary<uint, (Vector2 Position, uint Since)> _lineEndpoints = new();
    private int LineHoldMilliseconds => Math.Clamp(RecognitionOptions.HoldMilliseconds, 300, 1500);
    private const float LineHoldRadius = 3;
    private readonly Dictionary<uint, OverlayPointer> _touches = new();
    private readonly HashSet<uint> _suppressedTouches = new();
    private readonly List<uint> _scratchIds = new();
    private readonly TwoFingerManipulator _manipulator = new();
    private readonly TwoFingerManipulator _selectionManipulator = new();
    private readonly PalmRejector _palm = new();
    private readonly List<InkStroke> _recentForRecognition = new();
    private readonly Dictionary<uint, Vector2> _shapeDragStart = new();
    private List<InkStroke> _shapePreview = new();

    // Two-step shape state (Cuboid / Hyperbola, upstream drawMultiStepShape* / CuboidFrontRect*).
    private int _shapeStep;                 // 0 = first drag, 1 = second drag
    private Vector2 _stepAnchor;            // cuboid: normalized front top-left; hyperbola: locked center
    private Vector2 _stepSecond;            // cuboid: normalized front bottom-right
    private float _stepK;                   // hyperbola: asymptote slope |dy/dx| from step 1
    private List<InkStroke> _stepPending = new(); // step-1 ink kept visible until step 2 commits
    private Vector2 _lastEraserPoint;
    private bool _erasing;
    private uint? _panPointerId;
    private Vector2 _panStart;
    private Matrix3x2 _panView;

    public InkDocument Document { get; } = new();

    public UndoStack History { get; } = new(200);

    public bool HasActiveInteraction => _live.Count > 0 || _touches.Count > 0 || _erasing || IsLassoErasing
        || _activeHandle != SelectionHandle.None || _selectionGesture || _panPointerId is not null || _shapeStep != 0 || _shapeEditPointer is not null;

    public bool RestoreScreenContext(IEnumerable<InkStroke> strokes, UndoHistoryState history, Matrix3x2 view)
    {
        if (HasActiveInteraction) return false;
        EndPlayback(); FinishDotAnimations(); ClearSelection(); HideEraserPreview();
        ViewTransform = view;
        Document.ReplaceAll(strokes);
        History.Restore(history);
        NotifyElementsChanged();
        return true;
    }

    public StrokeAttributes CurrentAttributes { get; } = new()
    {
        Color = InkColor.Black,
        BaseWidth = 2.5f,
        TaperKind = TaperKind.Tail,
    };

    private InkTool _tool = InkTool.Pen;

    public InkTool Tool
    {
        get => _tool;
        set
        {
            EndPlayback();
            if (_tool == value) return;
            var previous = _tool;
            HideEraserPreview();
            _tool = value;
            ClearShapeHolds();
            if (_configuredPenWidth.HasValue)
                CurrentAttributes.BaseWidth = value == InkTool.Highlighter ? _configuredHighlighterWidth : _configuredPenWidth.Value;
            var color = value == InkTool.Highlighter ? _highlighterColor : _penColor;
            if (color.HasValue) CurrentAttributes.Color = color.Value;
            _panPointerId = null;
            CancelLassoErase();
            if (previous == InkTool.Eraser && _erasing) EndErase();
            if (previous == InkTool.Select)
            {
                ClearSelection();
                CloneMode = false;
            }
            // Leaving shape mode abandons any half-finished multi-step shape and clears
            // stickiness, like BtnPen_Click resetting isLongPressSelected (upstream:104-112).
            if (previous == InkTool.Shape)
            {
                ResetShapeState();
                StickyShape = false;
            }
            ToolChanged?.Invoke();
        }
    }

    /// <summary>Raised whenever <see cref="Tool"/> changes (incl. the auto-revert to pen after a shape).</summary>
    public event Action ToolChanged;
    private float? _configuredPenWidth;
    private float _configuredHighlighterWidth;
    private float? _inkWashWidth;
    public float InkWashWidth
    {
        get => _inkWashWidth ?? CurrentAttributes.BaseWidth;
        set => _inkWashWidth = value;
    }

    public StrokeAttributes GetDrawingAttributes(bool highlighter)
    {
        var attributes = CurrentAttributes.Clone();
        attributes.IsHighlighter = highlighter;
        if (_configuredPenWidth.HasValue && highlighter != (Tool == InkTool.Highlighter))
            attributes.BaseWidth = highlighter ? _configuredHighlighterWidth : _configuredPenWidth.Value;
        if (attributes.EffectiveTaper == TaperKind.InkWash) attributes.BaseWidth = InkWashWidth;
        return attributes;
    }
    private InkColor? _penColor, _highlighterColor;

    public void ConfigurePenColors(InkColor? pen, InkColor? highlighter)
    {
        _penColor = pen;
        _highlighterColor = highlighter;
        var color = Tool == InkTool.Highlighter ? highlighter : pen;
        if (color.HasValue) CurrentAttributes.Color = color.Value;
    }

    public void ConfigurePenWidths(float pen, float highlighter)
    {
        _configuredPenWidth = pen;
        _configuredHighlighterWidth = highlighter;
        CurrentAttributes.BaseWidth = Tool == InkTool.Highlighter ? highlighter : pen;
    }

    private ShapeKind _shapeKind = ShapeKind.Line;

    public ShapeKind ShapeKind
    {
        get => _shapeKind;
        set
        {
            // Every menu pick restarts multi-step state and clears stickiness, like the
            // original BtnDraw*_Click handlers (MW_ShapeDrawing.cs:124-467).
            ResetShapeState();
            _shapeKind = value;
            StickyShape = false;
            ShapeSelectionChanged?.Invoke();
        }
    }

    /// <summary>
    /// WPF 长按保持选中: while true the shape tool survives commits. Set by the menu's
    /// long-press path; a plain <see cref="ShapeKind"/> set always resets it to false.
    /// </summary>
    private bool _stickyShape;
    public bool StickyShape
    {
        get => _stickyShape;
        set
        {
            if (_stickyShape == value) return;
            _stickyShape = value;
            ShapeSelectionChanged?.Invoke();
        }
    }

    public event Action ShapeSelectionChanged;
    public event Action ElementsChanged;

    public void NotifyElementsChanged()
    {
        _drawingOrderDirty = true;
        ElementsChanged?.Invoke();
        // A selection drag updates every object before issuing its single frame invalidation.
        if (PreviewStrokes is null) Invalidated?.Invoke();
    }

    public void SelectElement(ISelectionElement element)
    {
        if (element is null || !element.IsPresent) return;
        Tool = InkTool.Select;
        ClearSelection();
        SelectedElements.Add(element);
        AddSelectionGroupMembers(element);
        SelectionChanged?.Invoke();
        Invalidated?.Invoke();
    }

    /// <summary>AI objects written in one gesture share a GroupId; selecting one selects the whole group.</summary>
    private void AddSelectionGroupMembers(ISelectionElement element)
    {
        if (element is not IGroupedSelectionElement grouped) return;
        string groupId = grouped.SelectionGroupId;
        if (string.IsNullOrEmpty(groupId) || GetSelectableElements is null) return;
        foreach (ISelectionElement candidate in GetSelectableElements())
            if (candidate.IsPresent && candidate is IGroupedSelectionElement sibling
                && string.Equals(sibling.SelectionGroupId, groupId, StringComparison.Ordinal)
                && !SelectedElements.Contains(candidate))
                SelectedElements.Add(candidate);
    }

    public void SetSelectionImageOpacity(float opacity)
    {
        if (!float.IsFinite(opacity)) return;
        opacity = Math.Clamp(opacity, 0, 1);
        var images = SelectedElements.OfType<IImageSelectionElement>()
            .Where(image => image.ImageData is { Length: > 0 } && image.Opacity != opacity).ToArray();
        if (images.Length == 0) return;
        var before = images.Select(image => image.Opacity).ToArray();
        var after = Enumerable.Repeat(opacity, images.Length).ToArray();
        Apply(after);
        History.Push(new DelegateAction("image opacity", () => Apply(after), () => Apply(before)));

        void Apply(float[] values)
        {
            for (int i = 0; i < images.Length; i++) images[i].Opacity = values[i];
            NotifyElementsChanged();
        }
    }

    /// <summary>Menu selection entry point: pick a kind and whether it stays selected (long-press).</summary>
    public void SelectShape(ShapeKind kind, bool sticky = false)
    {
        ShapeKind = kind;
        StickyShape = sticky;
        Tool = InkTool.Shape;
    }

    /// <summary>Dragging a selection can create copies while retaining the original selection.</summary>
    private bool _cloneMode;
    public bool CloneMode
    {
        get => _cloneMode;
        set { if (_cloneMode == value) return; _cloneMode = value; SelectionChanged?.Invoke(); Invalidated?.Invoke(); }
    }

    private float _eraserRadius = 16f;
    public float EraserRadius
    {
        get => _eraserRadius;
        set
        {
            if (_eraserRadius == value) return;
            _eraserRadius = value;
            if (IsEraserPreviewVisible) EraserPreviewChanged?.Invoke();
        }
    }

    private OverlayPointer? _eraserPreviewPointer;
    private bool _showPenCursor;
    public bool ShowPenCursor
    {
        get => _showPenCursor;
        set { if (_showPenCursor == value) return; _showPenCursor = value; EraserPreviewChanged?.Invoke(); }
    }
    public bool IsPenCursorVisible => ShowPenCursor && _eraserPreviewPointer is { Kind: not OverlayPointerKind.Touch }
        && Tool is InkTool.Pen or InkTool.Highlighter && Playback is null;
    public Rect PenCursorVisualBounds => IsPenCursorVisible && _eraserPreviewPointer is { } pointer
        ? Rect.FromLTRB(pointer.X - PenCursorRadius - 3, pointer.Y - PenCursorRadius - 3,
            pointer.X + PenCursorRadius + 3, pointer.Y + PenCursorRadius + 3) : Rect.Empty;
    public float PenCursorRadius => MathF.Max(2, (Tool == InkTool.Pen && CurrentAttributes.TaperKind == TaperKind.InkWash
        ? InkWashWidth : CurrentAttributes.BaseWidth) * MathF.Sqrt(MathF.Abs(ViewTransform.GetDeterminant())) / 2);
    public Vector2 PenCursorCenter => _eraserPreviewPointer is { } pointer ? new(pointer.X, pointer.Y) : Vector2.Zero;
    public event Action EraserPreviewChanged;
    public bool IsEraserPreviewVisible => _eraserPreviewPointer.HasValue && Playback is null
        && (_erasing ? _partialEraseGesture : Tool == InkTool.Eraser && EraserMode == InkEraserMode.Area);
    public Vector2 EraserPreviewCenter => _eraserPreviewPointer is { } pointer ? ToWorld(pointer) : Vector2.Zero;
    public float EraserPreviewRadius => _gestureEraserRadius ?? EraserRadius;
    public Rect EraserVisualBounds
    {
        get
        {
            if (!IsEraserPreviewVisible || _eraserPreviewPointer is not { } pointer) return Rect.Empty;
            float radius = EraserPreviewRadius;
            float rx = radius * MathF.Sqrt(ViewTransform.M11 * ViewTransform.M11 + ViewTransform.M21 * ViewTransform.M21);
            float ry = radius * MathF.Sqrt(ViewTransform.M12 * ViewTransform.M12 + ViewTransform.M22 * ViewTransform.M22);
            return Rect.FromLTRB(pointer.X - rx - 3, pointer.Y - ry - 3, pointer.X + rx + 3, pointer.Y + ry + 3);
        }
    }

    public void OnPointerHover(in OverlayPointer pointer)
    {
        if (pointer.Kind == OverlayPointerKind.Touch || _erasing || Playback is not null) return;
        UpdateEraserPreview(pointer);
    }

    public void HideEraserPreview(uint? pointerId = null)
    {
        if (_eraserPreviewPointer is not { } pointer || pointerId.HasValue && pointerId != pointer.Id) return;
        bool visible = IsEraserPreviewVisible || IsPenCursorVisible;
        _eraserPreviewPointer = null;
        if (visible) EraserPreviewChanged?.Invoke();
    }

    private void UpdateEraserPreview(in OverlayPointer pointer, bool notify = true)
    {
        if (_erasing && pointer.Id != _eraserPointerId) return;
        bool moved = _eraserPreviewPointer is not { } previous
            || previous.Id != pointer.Id || previous.X != pointer.X || previous.Y != pointer.Y;
        _eraserPreviewPointer = pointer;
        if (notify && moved && !_erasing && (IsEraserPreviewVisible || IsPenCursorVisible)) EraserPreviewChanged?.Invoke();
    }

    private InkEraserMode _eraserMode = InkEraserMode.Area;
    public InkEraserMode EraserMode
    {
        get => _eraserMode;
        set
        {
            if (_eraserMode == value) return;
            CancelLassoErase();
            if (_erasing) EndErase();
            HideEraserPreview();
            _eraserMode = value;
            if (Tool == InkTool.Eraser) ToolChanged?.Invoke();
        }
    }

    private readonly List<Vector2> _lassoPath = new();
    private Rect _lassoBounds;
    private uint _lassoPointerId;
    private Matrix3x2 _lassoViewAtStart;
    public bool IsLassoErasing { get; private set; }
    public IReadOnlyList<Vector2> LassoPath => _lassoPath;
    public Rect LassoVisualBounds => IsLassoErasing ? _lassoBounds.Inflate(4) : Rect.Empty;

    /// <summary>Palm/back-of-hand acts as an area eraser.</summary>
    public bool PalmErases { get; set; } = true;

    public bool RecognizeShapes { get; set; } = true;
    public bool UseHardwarePressure { get; set; } = true;
    public bool UsePenTilt { get; set; } = true;

    public bool MultiTouchEnabled { get; set; } = true;

    private Matrix3x2 _viewTransform = Matrix3x2.Identity;

    /// <summary>世界坐标 → 视图坐标的变换；变化时触发 <see cref="ViewChanged"/>。</summary>
    public Matrix3x2 ViewTransform
    {
        get => _viewTransform;
        set
        {
            if (value.Equals(_viewTransform)) return;
            _viewTransform = value;
            ViewChanged?.Invoke(value);
        }
    }

/// <summary>视图缩放 / 平移变化（参数为最新的 <see cref="ViewTransform"/>），供板书录制使用。</summary>
    public event Action<Matrix3x2> ViewChanged;

    /// <summary>设置视图变换；与直接赋值 <see cref="ViewTransform"/> 等价。</summary>
    public void SetView(Matrix3x2 transform) => ViewTransform = transform;

    /// <summary>
    /// 设置视图中心。仅当中心确实变化时触发 <see cref="ViewChanged"/>
    /// （中心本身不改变矩阵，订阅方仍会拿到当前变换）。
    /// </summary>
    public void SetViewCenter(Vector2 center)
    {
        if (center.Equals(ViewCenter)) return;
        ViewCenter = center;
        ViewChanged?.Invoke(ViewTransform);
    }


    public bool PanZoomEnabled { get; set; }
    public bool TouchGesturesEnabled { get; set; } = true;

    public float ZoomDamping
    {
        get => _manipulator.ZoomDamping;
        set { _manipulator.ZoomDamping = value; _selectionManipulator.ZoomDamping = value; }
    }

    public float ZoomDeadZone
    {
        get => _manipulator.ZoomDeadZone;
        set { _manipulator.ZoomDeadZone = value; _selectionManipulator.ZoomDeadZone = value; }
    }
    public void ConfigureViewScaleLimits(float minimum, float maximum)
    {
        _manipulator.MinScale = minimum;
        _manipulator.MaxScale = maximum;
    }
    public bool IsViewManipulating => _panPointerId is not null
        || TouchGesturesEnabled && PanZoomEnabled && Tool == InkTool.Pan && TryGetTouchPair(out _, out _)
        || TouchGesturesEnabled && !_selectionGesture && !MultiTouchEnabled && TryGetTouchPair(out _, out _) && Tool is not (InkTool.Select or InkTool.Eraser);

    public Vector2 ViewCenter { get; set; } = new(960, 540);

    /// <summary>1:1 port of the original gesture pivot option. 未验证。</summary>
    public Core.Settings.MatrixTransformCenterPointOptions TransformCenterOption { get; set; }

    private Vector2 ResolvePivot(Vector2 gestureCenter)
        => MatrixTransformCenter.Resolve(TransformCenterOption, ViewCenter, gestureCenter,
            SelectionBounds.Center, isLoaded: true);

    // Concrete collection types: foreach over these uses struct enumerators, so the per-frame
    // render loops do not box an IEnumerator (allocation-light hot path).
    public Dictionary<uint, InkStroke>.ValueCollection LiveStrokes => _live.Values;

    public List<InkStroke> ShapePreview => _shapePreview;

    public List<InkStroke> Selection { get; } = new();
    public List<ISelectionElement> SelectedElements { get; } = new();
    private Func<IEnumerable<ISelectionElement>> _getSelectableElements;
    public Func<IEnumerable<ISelectionElement>> GetSelectableElements
    {
        get => _getSelectableElements;
        set { _getSelectableElements = value; _drawingOrderDirty = true; }
    }
    public bool HasSelection => Selection.Count > 0 || SelectedElements.Count > 0;
    public bool IsMarqueeSelecting { get; private set; }
    public Rect MarqueeBounds { get; private set; }
    public bool IsRotatingSelection => _activeHandle == SelectionHandle.Rotate && _dragMoved;
    public float SelectionRotationDegrees => (MathF.Atan2(SelectionFrame.Transform.M12, SelectionFrame.Transform.M11)
        * 180 / MathF.PI % 360 + 360) % 360;
    public Rect SelectionVisualBounds
    {
        get
        {
            if (Tool != InkTool.Select) return Rect.Empty;
            var bounds = IsMarqueeSelecting ? MarqueeBounds.Inflate(3) : Rect.Empty;
            if (!HasSelection) return bounds;
            var frame = SelectionFrame;
            foreach (var p in frame.Corners(ViewTransform))
                bounds = bounds.Union(Rect.FromLTRB(p.X - 12, p.Y - 12, p.X + 12, p.Y + 12));
            var rotate = frame.HandlePosition(SelectionHandle.Rotate, ViewTransform);
            bounds = bounds.Union(Rect.FromLTRB(rotate.X - 12, rotate.Y - 12, rotate.X + 75, rotate.Y + 40));
            foreach (var control in ShapeControlPoints)
            {
                var p = Vector2.Transform(control, ViewTransform);
                bounds = bounds.Union(Rect.FromLTRB(p.X - 15, p.Y - 15, p.X + 15, p.Y + 15));
            }
            return bounds;
        }
    }

    /// <summary>When true, two-finger gestures transform the selection instead of the view.</summary>
    public bool GesturesTargetSelection { get; set; }

    /// <summary>World-space bounds of the current selection, empty when nothing is selected.</summary>
    public Rect SelectionBounds
    {
        get
        {
            if (_dragMoved) return Rendering.CommittedInkLayer.TransformBounds(_dragBounds, _selectionPreview);
            var r = Rect.Empty;
            foreach (var s in Selection) r = r.Union(IsPreviewStroke(s)
                ? Rendering.CommittedInkLayer.TransformBounds(s.Bounds, _selectionPreview) : s.Bounds);
            foreach (var element in SelectedElements) r = r.Union(element.Bounds);
            return r;
        }
    }

    // Selection drag state. Transforms are computed absolutely from a snapshot taken at
    // drag start, so a long drag never accumulates rounding error.
    private SelectionHandle _activeHandle = SelectionHandle.None;
    private Vector2 _dragFromView;
    private Matrix3x2 _viewAtDragStart;
    private List<InkStroke> _dragSnapshot;
    private List<InkStroke> _dragTargets;   // the selection, or the copies in a clone drag
    private bool _cloneDrag;
    private bool _controlCloneDrag;
    private InkStroke _controlClickStroke;
    private ISelectionElement _controlClickElement;
    private Rect _dragBounds;
    private bool _dragMoved;
    private uint _selectionPointerId;
    private SelectionFrame _selectionFrame;
    private SelectionFrame _dragFrame;
    private readonly List<(InkStroke Stroke, long Version)> _frameStrokes = new();
    private readonly List<(ISelectionElement Element, SelectionFrame Frame)> _frameElements = new();
    private List<ISelectionElement> _dragElements;
    private List<object> _dragElementSnapshot;
    // Marquee (rubber-band) selection state. Pure view/world geometry; no UI dependency.
    private bool _marqueeing;
    private Vector2 _marqueeStartView, _marqueeStartWorld, _marqueeCurrentView;

    /// <summary>Current marquee rectangle in view coordinates while dragging, otherwise null.</summary>
    public Rect? MarqueeViewRect
    {
        get
        {
            if (!_marqueeing) return null;
            var a = _marqueeStartView;
            var b = _marqueeCurrentView;
            return Rect.FromLTRB(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y),
                MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));
        }
    }

    private List<InkStroke> _marqueeInitialStrokes;
    private List<ISelectionElement> _marqueeInitialElements;
    private bool _selectionGesture;
    public bool IsSelectionGestureActive => _selectionGesture;
    private Matrix3x2 _selectionPreview = Matrix3x2.Identity;
    private bool DeferSelectionPoints => !(_hasShapeConnections && Selection.Any(s => s.ShapeSource?.Geometry is not null))
        && (_selectionGesture || _activeHandle is SelectionHandle.Move or SelectionHandle.Rotate);
    private readonly HashSet<InkStroke> _previewSet = new();
    public IReadOnlyList<InkStroke> PreviewStrokes => _dragMoved && DeferSelectionPoints ? _dragTargets : null;
    public Matrix3x2 SelectionPreviewTransform => _selectionPreview;
    public Rect SelectionPreviewSourceBounds => _dragBounds;
    public bool IsPreviewStroke(InkStroke stroke) => _dragMoved && DeferSelectionPoints && _previewSet.Contains(stroke);

    public SelectionFrame SelectionFrame
    {
        get
        {
            if (_dragMoved)
                return _dragFrame with { Transform = _dragFrame.Transform * _selectionPreview };
            bool valid = _frameStrokes.Count == Selection.Count && _frameElements.Count == SelectedElements.Count;
            for (int i = 0; valid && i < Selection.Count; i++)
                valid = ReferenceEquals(_frameStrokes[i].Stroke, Selection[i]) && _frameStrokes[i].Version == Selection[i].GeometryVersion;
            for (int i = 0; valid && i < SelectedElements.Count; i++)
                valid = ReferenceEquals(_frameElements[i].Element, SelectedElements[i]) && _frameElements[i].Frame == SelectedElements[i].Frame;
            if (!valid)
            {
                var frame = Selection.Count == 1 && SelectedElements.Count == 0 ? Selection[0].ObjectFrame
                    : Selection.Count == 0 && SelectedElements.Count == 1 ? SelectedElements[0].Frame
                    : new SelectionFrame(SelectionBounds, Matrix3x2.Identity);
                RememberFrame(frame);
            }
            return _selectionFrame;
        }
    }

    private void RememberFrame(SelectionFrame frame)
    {
        _selectionFrame = frame;
        _frameStrokes.Clear();
        foreach (var stroke in Selection) _frameStrokes.Add((stroke, stroke.GeometryVersion));
        _frameElements.Clear();
        foreach (var element in SelectedElements) _frameElements.Add((element, element.Frame));
    }

    private void RestoreSelectionFrame(List<InkStroke> strokes, List<ISelectionElement> elements, SelectionFrame frame)
    {
        if (Selection.SequenceEqual(strokes) && SelectedElements.SequenceEqual(elements)) RememberFrame(frame);
        else { _frameStrokes.Clear(); _frameElements.Clear(); }
    }

    public void ClearSelection()
    {
        if (_shapeEditPointer is { } editPointer)
            UpdateShapeControlDrag(new OverlayPointer(editPointer, 0, 0, .5f, 0, 0, OverlayPointerKind.Mouse, 0), true, canceled: true);
        ShapeEditEnabled = false;
        if (_selectionGesture) EndSelectionGesture();
        else if (_activeHandle != SelectionHandle.None) EndSelectionDrag();
        Selection.Clear(); SelectedElements.Clear();
        IsMarqueeSelecting = false; MarqueeBounds = Rect.Empty;
        _marqueeInitialStrokes = null; _marqueeInitialElements = null;
        _touches.Clear();
        SelectionChanged?.Invoke(); Invalidated?.Invoke();
    }

    public void SelectAll()
    {
        Tool = InkTool.Select;
        ClearSelection();
        Selection.AddRange(Document.Strokes);
        if (GetSelectableElements is not null) SelectedElements.AddRange(GetSelectableElements().Where(e => e.IsPresent));
        SelectionChanged?.Invoke(); Invalidated?.Invoke();
    }

    public PalmRejector Palm => _palm;

    /// <summary>Raised once per logical change; the presenter redraws on this.</summary>
    public event Action Invalidated;

    public event Action<InkStroke> StrokeCommitted;
    public event Action<IReadOnlyList<InkStroke>> StrokesErased;
    public event Action Cleared;

    public event Action SelectionChanged;

    public void ConfigureGestures(bool zoom, bool translate, bool rotate)
    {
        _manipulator.AllowZoom = zoom;
        _manipulator.AllowTranslate = translate;
        _manipulator.AllowRotation = rotate;
    }

    // ---- input ------------------------------------------------------------

    public void OnPointerDown(in OverlayPointer p)
    {
        if (Playback is not null) return;
        if (_shapeEditPointer is not null) return;
        if (Tool == InkTool.Select && BeginShapeControlDrag(p)) return;
        _palm.OnDown(p);
        if (p.Kind == OverlayPointerKind.Pen && _palm.PenHasPriority)
        {
            CancelTouchInk(p.InputSurface);
            foreach (var touch in _touches.Values)
                if (touch.InputSurface == p.InputSurface) _suppressedTouches.Add(touch.Id);
            foreach (var id in _suppressedTouches) _touches.Remove(id);
            _manipulator.Reset();
            if (_erasing && _palm.ActivePalms.Contains(_eraserPointerId)) EndErase();
        }
        if (_palm.SuppressesTouch(p)) { _suppressedTouches.Add(p.Id); return; }
        if (p.Kind == OverlayPointerKind.Touch && _palm.ShouldErase(p) && Tool is InkTool.Pan or InkTool.Select) return;
        UpdateEraserPreview(p, notify: false);
        if (Tool == InkTool.Pan)
        {
            if (TouchGesturesEnabled && PanZoomEnabled && p.Kind == OverlayPointerKind.Touch)
            {
                _touches[p.Id] = p;
                if (_touches.Count >= 2 && TryGetTouchPair(out var a, out var b, p.InputSurface))
                {
                    _panPointerId = null;
                    _manipulator.Begin(new(a.X, a.Y), new(b.X, b.Y));
                    return;
                }
            }
            if (_panPointerId is null)
            {
                _panPointerId = p.Id; _panStart = new Vector2(p.X, p.Y); _panView = ViewTransform;
            }
            return;
        }
        var world = ToWorld(p);
        if (Tool == InkTool.Eraser && EraserMode == InkEraserMode.Lasso)
        {
            if (!IsLassoErasing && !_erasing) BeginLassoErase(p);
            return;
        }
        if (Tool == InkTool.Select)
        {
            if (p.Kind == OverlayPointerKind.Touch)
            {
                _touches[p.Id] = p;
                if (_selectionGesture)
                {
                    if (_selectionTouchPair is { } pair && pair.First == pair.Second && pair.First != p.Id
                        && _touches.TryGetValue(pair.First, out var first) && first.InputSurface == p.InputSurface
                        && SelectionFrame.HitTest(new Vector2(p.X, p.Y), ViewTransform) != SelectionHandle.None)
                        BeginSelectionTouchPair(first, p);
                    return;
                }
                if (HasSelection && !IsMarqueeSelecting
                    && (_activeHandle == SelectionHandle.None || _touches.ContainsKey(_selectionPointerId))
                    && TryGetSelectionTouchPair(out var a, out var b, p.InputSurface))
                {
                    BeginSelectionGesture();
                    BeginSelectionTouchPair(a, b);
                    return;
                }
            }
            if ((_activeHandle != SelectionHandle.None || IsMarqueeSelecting) && p.Id != _selectionPointerId) return;
            BeginSelectionInteraction(p, world);
            return;
        }

        if (p.Kind == OverlayPointerKind.Touch)
        {
            // An explicitly selected eraser must keep its mode/size instead of being
            // reclassified as an implicit palm eraser or a slideshow swipe.
            if (Tool == InkTool.Eraser)
            {
                if (!_erasing) BeginErase(p.Id, world, partialStroke: EraserMode == InkEraserMode.Area);
                return;
            }
            // A palm must never form a pinch pair with the writing finger.
            if (PalmErases && _palm.ShouldErase(p))
            {
                if (!_erasing)
                {
                    if (_palm.Advanced is not null)
                        _gestureEraserRadius = MathF.Max(EraserRadius, MathF.Max(p.ContactWidth, p.ContactHeight) / 2);
                    BeginErase(p.Id, world, partialStroke: true);
                }
                return;
            }
            // WPF MW_TouchEvents.cs:275-291: isInMultiTouchMode==true means multi-finger
            // WRITING (gestures disabled); gestures run only when the mode is OFF. The second
            // finger then cancels partial touch ink and starts the gesture (first-contact
            // snapshot rollback on lift, MW_TouchEvents.cs:273-321).
            // 1:1 special-screen classification (QuadIR / TouchMultiplier / nib-vs-finger bounds).
            // 未验证：按要求跳过行为验证。
            if (_palm.Advanced is not null)
            {
                var decision = _palm.ClassifyContact(p);
                switch (decision.Intent)
                {
                    case TouchIntent.Ignore:
                        return;
                    case TouchIntent.GestureOnly:
                        _slideGestureStartX = p.X;
                        _slideGestureActive = true;
                        return;
                    case TouchIntent.EraseByPoint when PalmErases:
                        _gestureEraserRadius = (float)(decision.EraserDiameter / 2.0);
                        BeginErase(p.Id, world, partialStroke: true);
                        return;
                    case TouchIntent.EraseByStroke when PalmErases:
                        _gestureEraserRadius = 2.5f;
                        BeginErase(p.Id, world, partialStroke: false);
                        return;
                }
            }

            if (!_palm.ShouldDraw(p) && Tool is InkTool.Pen or InkTool.Highlighter) return;
            _touches[p.Id] = p;
            if (TouchGesturesEnabled && !MultiTouchEnabled && TryGetTouchPair(out var a, out var b, p.InputSurface))
            {
                CancelTouchInk(p.InputSurface);
                _manipulator.Begin(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y));
                Invalidated?.Invoke();
                return;
            }
        }

        switch (Tool)
        {
            case InkTool.Eraser:
                if (!_erasing) BeginErase(p.Id, world, partialStroke: EraserMode == InkEraserMode.Area);
                break;

            case InkTool.Select:
                BeginSelectionInteraction(p, world);
                break;

            case InkTool.Shape:
                // Hyperbola step 2 keeps iniP locked at the center (NeedUpdateIniP,
                // upstream MW_ShapeDrawing.cs:1646-1653); the cuboid depth drag only uses endP.
                _shapeDragStart[p.Id] = _shapeStep == 1 && ShapeGenerator.IsHyperbola(ShapeKind)
                    ? _stepAnchor
                    : world;
                break;

            default:
                StartStroke(p, world);
                break;
        }
    }

    public void OnPointerMove(in OverlayPointer p)
    {
        if (Playback is not null) return;
        if (_shapeEditPointer is not null) { UpdateShapeControlDrag(p, false); return; }
        if (_suppressedTouches.Contains(p.Id)) return;
        _palm.OnMove(p);
        if (p.Kind == OverlayPointerKind.Touch && PalmErases && _palm.ShouldErase(p)
            && Tool is InkTool.Pen or InkTool.Highlighter or InkTool.Shape && !_erasing)
        {
            _live.Remove(p.Id); _strokeStartTimes.Remove(p.Id); _lineEndpoints.Remove(p.Id);
            RemoveShapeHold(p.Id);
            _shapeDragStart.Remove(p.Id); _touches.Remove(p.Id);
            _manipulator.Reset();
            _gestureEraserRadius = MathF.Max(EraserRadius, MathF.Max(p.ContactWidth, p.ContactHeight) / 2);
            BeginErase(p.Id, ToWorld(p), partialStroke: true);
            Invalidated?.Invoke();
        }
        UpdateEraserPreview(p);
        if (Tool == InkTool.Pan)
        {
            if (TouchGesturesEnabled && PanZoomEnabled && p.Kind == OverlayPointerKind.Touch && _touches.ContainsKey(p.Id))
            {
                _touches[p.Id] = p;
                if (_touches.Count >= 2 && TryGetTouchPair(out var a, out var b, p.InputSurface))
                {
                    var view = ViewTransform;
                    if (_manipulator.Update(new(a.X, a.Y), new(b.X, b.Y), ref view, new((a.X + b.X) / 2, (a.Y + b.Y) / 2), screenSpace: true))
                    { ViewTransform = view; Invalidated?.Invoke(); }
                    return;
                }
            }
            if (_panPointerId == p.Id)
            {
                ViewTransform = _panView * Matrix3x2.CreateTranslation(new Vector2(p.X, p.Y) - _panStart);
                Invalidated?.Invoke();
            }
            return;
        }
        if (IsLassoErasing)
        {
            if (p.Id == _lassoPointerId) AppendLassoPoint(new Vector2(p.X, p.Y));
            return;
        }
        var world = ToWorld(p);
        if (_selectionGesture)
        {
            if (!_touches.ContainsKey(p.Id)) return;
            _touches[p.Id] = p;
            if (_selectionTouchPair is { } pair && (pair.First == p.Id || pair.Second == p.Id)
                && _touches.TryGetValue(pair.First, out var a) && _touches.TryGetValue(pair.Second, out var b))
            {
                var first = new Vector2(a.X, a.Y);
                var second = new Vector2(b.X, b.Y);
                var transform = _selectionManipulator.TransformFromStart(first, second);
                if (transform == Matrix3x2.Identity) SetSelectionGestureTransform(_selectionTouchStartPreview);
                else if (Matrix3x2.Invert(_viewAtDragStart, out var inverse))
                    SetSelectionGestureTransform(_selectionTouchStartPreview * _viewAtDragStart * transform * inverse);
            }
            return;
        }
        if (IsMarqueeSelecting)
        {
            if (p.Id == _selectionPointerId) UpdateMarquee(new Vector2(p.X, p.Y));
            return;
        }

        if (p.Kind == OverlayPointerKind.Touch && _touches.ContainsKey(p.Id))
        {
            _touches[p.Id] = p;
            if (TouchGesturesEnabled && !MultiTouchEnabled && Tool != InkTool.Eraser && TryGetTouchPair(out var ta, out var tb, p.InputSurface))
            {
                if (GesturesTargetSelection && Selection.Count > 0)
                {
                    // Manipulate the selection rather than the whole view.
                    var identity = Matrix3x2.Identity;
                    // WPF GridInkCanvasSelectionCover_ManipulationDelta uses the settings-driven
                    // pivot (GetMatrixTransformCenterPoint), not always the selection centre.
                    if (_manipulator.Update(ToWorld(ta), ToWorld(tb), ref identity, ResolvePivot((ToWorld(ta) + ToWorld(tb)) * 0.5f)))
                    {
                        TransformSelectionByGesture(identity);
                        Invalidated?.Invoke();
                    }
                    return;
                }

                var t = ViewTransform;
                var a = new Vector2(ta.X, ta.Y);
                var b = new Vector2(tb.X, tb.Y);
                var pivot = MatrixTransformCenter.Resolve(TransformCenterOption, ViewCenter, (a + b) * .5f,
                    Vector2.Transform(SelectionBounds.Center, ViewTransform), isLoaded: true);
                if (_manipulator.Update(a, b, ref t, pivot, screenSpace: true))
                {
                    ViewTransform = t;
                    Invalidated?.Invoke();
                }
                return;
            }
        }

        if (_activeHandle != SelectionHandle.None)
        {
            if (p.Id == _selectionPointerId) UpdateSelectionDrag(new Vector2(p.X, p.Y), p.Shift, p.Control);
            return;
        }

        if (_erasing)
        {
            if (p.Id != _eraserPointerId) return;
            bool changed = EraseAlong(_lastEraserPoint, world);
            _lastEraserPoint = world;
            if (_partialEraseGesture && !changed) EraserPreviewChanged?.Invoke();
            return;
        }

        if (Tool == InkTool.Shape && _shapeDragStart.TryGetValue(p.Id, out var start))
        {
            _shapePreview = GenerateShapePreview(start, world);
            Invalidated?.Invoke();
            return;
        }

        if (_live.TryGetValue(p.Id, out var stroke))
        {
            if (_heldShapes.ContainsKey(p.Id)) { RefineHeldShape(p.Id, world); return; }
            var position = new Vector2(p.X, p.Y);
            if (!_lineEndpoints.TryGetValue(p.Id, out var endpoint)
                || Vector2.DistanceSquared(position, endpoint.Position) > LineHoldRadius * LineHoldRadius)
            {
                _lineEndpoints[p.Id] = (position, p.TimeMs);
                ResetShapeHold(p.Id);
            }
            // Widths are settled here, as the point arrives — never after pen-up.
            float time = _strokeStartTimes.TryGetValue(p.Id, out uint startTime) ? unchecked(p.TimeMs - startTime) : p.TimeMs;
            stroke.AddPoint(world.X, world.Y, p.Pressure, time, UseHardwarePressure && p.Kind == OverlayPointerKind.Pen && p.HasPressure,
                UsePenTilt ? p.TiltX : 0, UsePenTilt ? p.TiltY : 0);
            Invalidated?.Invoke();
        }
    }

    public void OnPointerUp(in OverlayPointer p, bool canceled = false)
    {
        if (Playback is not null) return;
        if (_shapeEditPointer is not null) { UpdateShapeControlDrag(p, true, canceled); return; }
        if (_suppressedTouches.Remove(p.Id)) { _palm.OnUp(p); return; }
        if (p.Kind == OverlayPointerKind.Touch) HideEraserPreview(p.Id);
        else UpdateEraserPreview(p);
        if (Tool == InkTool.Pan)
        {
            OnPointerMove(p);
            if (_panPointerId == p.Id) _panPointerId = null;
            if (PanZoomEnabled)
            {
                _touches.Remove(p.Id);
                if (_touches.Count < 2) _manipulator.Reset();
                if (_touches.Count == 1)
                {
                    var remaining = _touches.Values.First();
                    _panPointerId = remaining.Id; _panStart = new(remaining.X, remaining.Y); _panView = ViewTransform;
                }
            }
            Invalidated?.Invoke();
            _palm.OnUp(p); return;
        }
        if (IsLassoErasing)
        {
            _palm.OnUp(p);
            if (p.Id == _lassoPointerId)
            {
                AppendLassoPoint(new Vector2(p.X, p.Y), final: true);
                CommitLassoErase();
            }
            return;
        }
        if (_selectionGesture)
        {
            OnPointerMove(p);
            _palm.OnUp(p); _touches.Remove(p.Id);
            if (_selectionTouchPair is { } pair && (pair.First == p.Id || pair.Second == p.Id))
            {
                if (TryGetSelectionTouchPair(out var a, out var b, p.InputSurface)) BeginSelectionTouchPair(a, b);
                else if (_touches.TryGetValue(pair.First == p.Id ? pair.Second : pair.First, out var remaining))
                    BeginSelectionTouchPair(remaining, remaining);
                else EndSelectionGesture();
            }
            return;
        }
        bool viewGestureEnded = IsViewManipulating;
        if (viewGestureEnded) OnPointerMove(p);
        _palm.OnUp(p);
        _touches.Remove(p.Id);
        if (_touches.Count < 2) _manipulator.Reset();
        if (viewGestureEnded) Invalidated?.Invoke();

        var world = ToWorld(p);
        if (IsMarqueeSelecting)
        {
            if (p.Id != _selectionPointerId) return;
            UpdateMarquee(new Vector2(p.X, p.Y));
            IsMarqueeSelecting = false; MarqueeBounds = Rect.Empty;
            _marqueeInitialStrokes = null; _marqueeInitialElements = null;
            SelectionChanged?.Invoke(); Invalidated?.Invoke();
            return;
        }

        // Finger slide control in a slideshow (original GestureOnly mode). 未验证。
        if (_slideGestureActive)
        {
            _slideGestureActive = false;
            float dx = p.X - _slideGestureStartX;
            if (MathF.Abs(dx) > 60f) SlideFlipRequested?.Invoke(dx < 0);
            return;
        }

        if (_activeHandle != SelectionHandle.None)
        {
            if (p.Id != _selectionPointerId) return;
            UpdateSelectionDrag(new Vector2(p.X, p.Y), p.Shift, p.Control);
            EndSelectionDrag();
            return;
        }

        if (_erasing)
        {
            if (p.Id != _eraserPointerId) return;
            // The final up can carry a new position even when no move packet was delivered.
            EraseAlong(_lastEraserPoint, world);
            EndErase();
            return;
        }

        if (Tool == InkTool.Shape && _shapeDragStart.Remove(p.Id, out var start))
        {
            CommitShapeDrag(start, world);
            return;
        }

        if (_live.Remove(p.Id, out var stroke))
        {
            if (!canceled && stroke.UsesHardwarePressure && !_heldShapes.ContainsKey(p.Id)
                && Vector2.DistanceSquared(stroke.Points[^1].Position, world) > .01f)
            {
                var last = stroke.Points[^1];
                bool pressureOnLift = UseHardwarePressure && p.HasPressure && float.IsFinite(p.Pressure) && p.Pressure > 0;
                float time = _strokeStartTimes.TryGetValue(p.Id, out uint startTime) ? unchecked(p.TimeMs - startTime) : p.TimeMs;
                // Pointer-up often reports zero after contact is already lost. Extend to its
                // final coordinate with the last contact pressure in that case.
                stroke.AddPoint(world.X, world.Y, pressureOnLift ? p.Pressure : last.Pressure, time,
                    pressureOnLift || last.HasPressure, UsePenTilt ? p.TiltX : 0, UsePenTilt ? p.TiltY : 0);
            }
            if (_heldShapes.ContainsKey(p.Id) && !canceled) RefineHeldShape(p.Id, world);
            var shapeHold = RemoveShapeHold(p.Id);
            _strokeStartTimes.Remove(p.Id);
            bool held = _lineEndpoints.Remove(p.Id, out var endpoint)
                && Vector2.DistanceSquared(new Vector2(p.X, p.Y), endpoint.Position) <= LineHoldRadius * LineHoldRadius
                && unchecked((int)(p.TimeMs - endpoint.Since)) >= LineHoldMilliseconds;
            if (!canceled) stroke = ConfirmTap(stroke, world, p.Kind);
            if (stroke.Count >= 1) CommitStrokes(new List<InkStroke> { stroke }, "ink", held, canceled ? null : shapeHold);
            if (_dotAnimations.Count > 0) DotAnimationChanged?.Invoke();
            Invalidated?.Invoke();
        }
    }

    private InkStroke ConfirmTap(InkStroke stroke, Vector2 end, OverlayPointerKind kind)
    {
        if (stroke.Count == 0) return stroke;
        var first = stroke.Points[0];
        var firstScreen = Vector2.Transform(first.Position, ViewTransform);
        var endScreen = Vector2.Transform(end, ViewTransform);
        float scale = MathF.Sqrt(MathF.Abs(ViewTransform.GetDeterminant()));
        float screenWidth = stroke.Attributes.BaseWidth * scale;
        float tolerance = kind == OverlayPointerKind.Touch
            ? Math.Clamp(screenWidth * .35f, 4, 6)
            : Math.Clamp(screenWidth * .25f, 1.5f, 3);
        var minimum = Vector2.Min(firstScreen, endScreen);
        var maximum = Vector2.Max(firstScreen, endScreen);
        if (maximum.X - minimum.X > tolerance * 2 || maximum.Y - minimum.Y > tolerance * 2) return stroke;
        foreach (var point in stroke.Points)
        {
            var screen = Vector2.Transform(point.Position, ViewTransform);
            minimum = Vector2.Min(minimum, screen);
            maximum = Vector2.Max(maximum, screen);
            if (maximum.X - minimum.X > tolerance * 2 || maximum.Y - minimum.Y > tolerance * 2) return stroke;
        }

        var center = (minimum + maximum) / 2;
        if (Vector2.DistanceSquared(center, endScreen) > tolerance * tolerance) return stroke;
        foreach (var point in stroke.Points)
            if (Vector2.DistanceSquared(center, Vector2.Transform(point.Position, ViewTransform)) > tolerance * tolerance) return stroke;
        if (!Matrix3x2.Invert(ViewTransform, out var inverse)) return stroke;
        var worldCenter = Vector2.Transform(center, inverse);

        var dotPoint = first;
        float targetWidth = stroke.Attributes.BaseWidth;
        if (stroke.UsesHardwarePressure)
        {
            targetWidth = 0;
            foreach (var point in stroke.Points)
                if (point.Width > targetWidth) { targetWidth = point.Width; dotPoint = point; }
        }
        var dot = new InkStroke(stroke.Attributes.Clone());
        dot.RestorePoints(new[] { new InkPoint(worldCenter.X, worldCenter.Y, dotPoint.Pressure, targetWidth, first.TimeMs,
            stroke.UsesHardwarePressure || dotPoint.HasPressure, dotPoint.TiltX, dotPoint.TiltY) });
        if (stroke.Count > 1 || Vector2.DistanceSquared(first.Position, worldCenter) > .001f
            || MathF.Abs(MathF.Max(.5f, first.Width) - MathF.Max(.5f, targetWidth)) > .001f)
        {
            _dotAnimations.Add(new InkDotAnimation(dot, stroke));
            DotAnimationVersion++;
        }
        return dot;
    }

    private void StartStroke(in OverlayPointer p, Vector2 world)
    {
        var attrs = GetDrawingAttributes(Tool == InkTool.Highlighter);
        var stroke = new InkStroke(attrs);
        bool relativeTime = attrs.EffectiveTaper is >= TaperKind.SmoothPen and <= TaperKind.InkWash
            || UseHardwarePressure && p.Kind == OverlayPointerKind.Pen && p.HasPressure;
        if (relativeTime) _strokeStartTimes[p.Id] = p.TimeMs;
        stroke.AddPoint(world.X, world.Y, p.Pressure, relativeTime ? 0 : p.TimeMs,
            UseHardwarePressure && p.Kind == OverlayPointerKind.Pen && p.HasPressure, UsePenTilt ? p.TiltX : 0, UsePenTilt ? p.TiltY : 0);
        _live[p.Id] = stroke;
        _lineEndpoints[p.Id] = (new Vector2(p.X, p.Y), p.TimeMs);
        ResetShapeHold(p.Id);
        Invalidated?.Invoke();
    }

    // ---- shape drawing ------------------------------------------------------

    /// <summary>
    /// Finish a partial cuboid face or explicitly requested asymptotes when switching tools.
    /// </summary>
    private void ResetShapeState()
    {
        if (_stepPending.Count > 0 && (ShapeGenerator.IsTwoStepSolid(ShapeKind) || ShapeGenerator.HasHyperbolaAsymptotes(ShapeKind)))
        {
            var pending = _stepPending;
            _stepPending = new List<InkStroke>();
            CommitStrokes(pending, "shape");
        }
        _shapeStep = 0;
        _stepPending.Clear();
        _stepK = 0;
        _stepAnchor = default;
        _stepSecond = default;
        _shapePreview = new List<InkStroke>();
        _shapeDragStart.Clear();
    }

    /// <summary>WPF shapes are uniform-width: the handwriting taper never applies to shape ink.</summary>
    private StrokeAttributes ShapeAttributes()
    {
        var a = CurrentAttributes.Clone();
        a.TaperKind = TaperKind.None;
        return a;
    }

    private List<InkStroke> GenerateShapePreview(Vector2 start, Vector2 end)
    {
        if (_shapeStep == 1)
        {
            if (ShapeGenerator.IsTwoStepSolid(ShapeKind))
            {
                var offset = SolidOffset(start, end);
                var screenOffset = Vector2.TransformNormal(offset, ViewTransform);
                return screenOffset.LengthSquared() < 9 ? new List<InkStroke>(_stepPending)
                    : ShapeGenerator.GenerateSolid(ShapeKind, _stepAnchor, _stepSecond, offset, ShapeAttributes());
            }
            if (ShapeGenerator.IsHyperbola(ShapeKind))
            {
                return _stepPending
                    .Concat(ShapeGenerator.GenerateHyperbolaBranches(_stepAnchor, _stepK, end,
                        ShapeGenerator.HasHyperbolaFoci(ShapeKind), ShapeAttributes()))
                    .ToList();
            }
        }
        return ShapeGenerator.Generate(ShapeKind, start, end, ShapeAttributes());
    }

    private Vector2 SolidOffset(Vector2 start, Vector2 end)
        => ShapeKind == ShapeKind.SquarePyramid ? end - (_stepAnchor + _stepSecond) / 2 : end - start;

    private void CommitShapeDrag(Vector2 start, Vector2 end)
    {
        var kind = ShapeKind;
        var attrs = ShapeAttributes();

        if (ShapeGenerator.IsTwoStepSolid(kind))
        {
            if (_shapeStep == 0)
            {
                end = ShapeGenerator.SolidBaseEnd(kind, start, end);
                float scale = MathF.Sqrt(MathF.Abs(ViewTransform.GetDeterminant()));
                if (MathF.Abs(end.X - start.X) * scale < 3 || MathF.Abs(end.Y - start.Y) * scale < 3)
                {
                    _shapePreview.Clear(); Invalidated?.Invoke(); return;
                }
                // Hold the first face as preview until the signed depth/apex drag commits.
                _stepPending = ShapeGenerator.Generate(kind, start, end, attrs);
                _stepAnchor = new Vector2(MathF.Min(start.X, end.X), MathF.Min(start.Y, end.Y));
                _stepSecond = new Vector2(MathF.Max(start.X, end.X), MathF.Max(start.Y, end.Y));
                _shapeStep = 1;
                _shapePreview = new List<InkStroke>(_stepPending);
                Invalidated?.Invoke();
                return; // stay in shape mode for the depth drag
            }
            // The signed second drag places the opposite face. Commit all edges in one undo unit.
            var offset = SolidOffset(start, end);
            if (Vector2.TransformNormal(offset, ViewTransform).LengthSquared() < 9)
            {
                _shapePreview = new List<InkStroke>(_stepPending); Invalidated?.Invoke(); return;
            }
            var solid = ShapeGenerator.GenerateSolid(kind, _stepAnchor, _stepSecond, offset, attrs);
            if (solid.Count == 0) return;
            _stepPending = new List<InkStroke>();
            _shapeStep = 0;
            _shapePreview = new List<InkStroke>();
            CommitStrokes(solid, "shape");
            if (!StickyShape) Tool = InkTool.Pen;
            return;
        }

        if (ShapeGenerator.IsHyperbola(kind))
        {
            if (_shapeStep == 0)
            {
                // Step 1: dashed asymptotes lock k=|dy/dx| (upstream:894-902,1550-1555).
                var asymptotes = ShapeGenerator.Generate(kind, start, end, attrs);
                if (asymptotes.Count == 0) { Invalidated?.Invoke(); return; } // degenerate drag: retry
                _stepPending = asymptotes;
                _stepK = MathF.Abs((end.Y - start.Y) / (end.X - start.X));
                _stepAnchor = start;
                _stepSecond = end;
                _shapeStep = 1;
                _shapePreview = new List<InkStroke>(_stepPending);
                Invalidated?.Invoke();
                return;
            }
            var branches = ShapeGenerator.GenerateHyperbolaBranches(_stepAnchor, _stepK, end,
                ShapeGenerator.HasHyperbolaFoci(kind), attrs);
            if (branches.Count == 0) { Invalidated?.Invoke(); return; }
            var kept = _stepPending;
            _stepPending = new List<InkStroke>();
            _shapeStep = 0;
            _shapePreview.Clear();
            var hyperbola = ShapeGenerator.HasHyperbolaAsymptotes(kind) ? kept.Concat(branches).ToList() : branches;
            ShapeDefinition.Attach(hyperbola, new ShapeDefinition { Kind = kind, Construction = ShapeConstruction.Hyperbola,
                Parameters = new[] { _stepAnchor.X, _stepAnchor.Y, _stepSecond.X, _stepSecond.Y, end.X, end.Y } });
            if (hyperbola.Count > 0) CommitStrokes(hyperbola, "shape");
            if (!StickyShape) Tool = InkTool.Pen;
            return;
        }

        var shapes = ShapeGenerator.Generate(kind, start, end, attrs);
        _shapePreview = new List<InkStroke>();
        if (shapes.Count > 0) CommitStrokes(shapes, "shape");
        Invalidated?.Invoke();
        // 画完一次还原到笔模式 unless long-press sticky (upstream:1502-1518).
        if (shapes.Count > 0 && !StickyShape) Tool = InkTool.Pen;
    }

    /// <summary>
    /// Complete + move to document + record history, then invalidate exactly once.
    /// </summary>
    private void CommitStrokes(List<InkStroke> strokes, string name, bool lineEndpointHeld = false, HeldShape heldShape = null)
    {
        long layer = NextLayerOrder();
        foreach (var stroke in strokes) stroke.LayerOrder = layer++;
        foreach (var s in strokes) s.Complete();
        using (Document.DeferNotifications())
        {
            Document.AddRange(strokes);
            _drawingOrderDirty = true;
            History.Push(new DelegateAction(name,
                () => Document.AddRange(strokes),
                () => Document.RemoveAll(strokes)));
foreach (var s in strokes) StrokeCommitted?.Invoke(s);
            if (RecognizeShapes && name == "ink") TryRecognize(strokes, lineEndpointHeld, heldShape);
        }
        Invalidated?.Invoke();
    }

    // ---- erasing ----------------------------------------------------------

    private void BeginLassoErase(in OverlayPointer pointer)
    {
        _lassoPath.Clear();
        _lassoPointerId = pointer.Id;
        _lassoViewAtStart = ViewTransform;
        var point = new Vector2(pointer.X, pointer.Y);
        _lassoBounds = new Rect(point.X, point.Y, point.X, point.Y);
        IsLassoErasing = true;
        AppendLassoPoint(point, final: true);
    }

    private void AppendLassoPoint(Vector2 point, bool final = false)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) return;
        if (_lassoPath.Count > 0 && Vector2.DistanceSquared(_lassoPath[^1], point) < (final ? 0.001f : 4f)) return;
        // Bound memory and path-rendering work during very long gestures.
        if (_lassoPath.Count >= 4096)
        {
            int count = 1;
            for (int i = 2; i < _lassoPath.Count; i += 2) _lassoPath[count++] = _lassoPath[i];
            _lassoPath.RemoveRange(count, _lassoPath.Count - count);
        }
        _lassoPath.Add(point);
        _lassoBounds = new Rect(MathF.Min(_lassoBounds.Left, point.X), MathF.Min(_lassoBounds.Top, point.Y),
            MathF.Max(_lassoBounds.Right, point.X), MathF.Max(_lassoBounds.Bottom, point.Y));
        Invalidated?.Invoke();
    }

    private void CommitLassoErase()
    {
        var lasso = new LassoGeometry(_lassoPath);
        var view = _lassoViewAtStart;
        // Clear the transient path before document notifications can render a committed frame.
        IsLassoErasing = false; _lassoPath.Clear(); _lassoBounds = Rect.Empty;
        if (lasso.IsValid)
        {
            var removed = Document.Strokes.Select((stroke, index) => (Index: index, Stroke: stroke))
                .Where(item => lasso.IntersectsStroke(item.Stroke, view)).ToList();
            var shapeGroups = removed.Where(item => item.Stroke.ShapeSource?.Geometry is not null).Select(item => item.Stroke.ShapeSource.GroupId).ToHashSet();
            if (shapeGroups.Count > 0)
            {
                var hits = removed.Select(item => item.Stroke).ToHashSet();
                removed = Document.Strokes.Select((stroke, index) => (Index: index, Stroke: stroke))
                    .Where(item => hits.Contains(item.Stroke) || item.Stroke.ShapeSource is { } source && shapeGroups.Contains(source.GroupId)).ToList();
            }
            var elements = GetSelectableElements?.Invoke()
                .Where(e => e.IsPresent && lasso.IntersectsFrame(e.Frame, view)).ToList() ?? new();
            if (removed.Count > 0 || elements.Count > 0)
            {
                var strokes = removed.Select(item => item.Stroke).ToList();
                Remove();
                History.Push(new DelegateAction("lasso erase", Remove, Restore));
                if (strokes.Count > 0) StrokesErased?.Invoke(strokes);
                PruneSelection();

                void Remove()
                {
                    Document.RemoveAll(strokes);
                    foreach (var element in elements) element.SetPresent(false);
                }
                void Restore()
                {
                    Document.InsertRange(removed);
                    // Reinsert in reverse removal order to preserve object stacking.
                    for (int i = elements.Count - 1; i >= 0; i--) elements[i].SetPresent(true);
                }
            }
        }
        Invalidated?.Invoke();
    }

    public void CancelLassoErase()
    {
        if (!IsLassoErasing) return;
        IsLassoErasing = false; _lassoPath.Clear(); _lassoBounds = Rect.Empty;
        Invalidated?.Invoke();
    }

    /// <summary>Cancelled lasso input never deletes; other tools keep their final-sample behavior.</summary>
    public void OnPointerCanceled(in OverlayPointer pointer)
    {
        // Capture loss can commit the last sample, but cannot express an intentional hold.
        _lineEndpoints.Remove(pointer.Id);
        if (IsLassoErasing)
        {
            _palm.OnUp(pointer);
            if (pointer.Id == _lassoPointerId) CancelLassoErase();
            return;
        }
        OnPointerUp(pointer, canceled: true);
        HideEraserPreview(pointer.Id);
    }

    private readonly List<InkStroke> _erasedThisGesture = new();
    private List<InkStroke> _eraseBeforeSnapshot;
    private bool _partialEraseGesture;
    private uint _eraserPointerId;

    /// <summary>Per-contact eraser size from the classifier; falls back to the configured size.</summary>
    private float? _gestureEraserRadius;

    private bool _slideGestureActive;
    private float _slideGestureStartX;

    /// <summary>Raised when a finger swipe should flip slides: true = next, false = previous.</summary>
    public event Action<bool> SlideFlipRequested;

    private void BeginErase(uint pointerId, Vector2 world, bool partialStroke)
    {
        _eraserPointerId = pointerId;
        _erasing = true;
        _partialEraseGesture = partialStroke;
        _lastEraserPoint = world;
        _erasedThisGesture.Clear();
        _eraseBeforeSnapshot = partialStroke
            ? Document.Strokes.ToList()
            : null;
        bool changed = EraseAlong(world, world);
        if (partialStroke && !changed) EraserPreviewChanged?.Invoke();
    }

    private bool EraseAlong(Vector2 from, Vector2 to)
    {
        float radius = _gestureEraserRadius ?? EraserRadius;
        // Sample along the sweep so a fast wipe cannot skip strokes.
        float dist = Vector2.Distance(from, to);
        int steps = Math.Max(1, (int)(dist / MathF.Max(4f, radius * 0.5f)));
        bool any = false;
        using (Document.DeferNotifications())
        for (int i = dist == 0 ? steps : 1; i <= steps; i++)
        {
            var p = Vector2.Lerp(from, to, steps == 0 ? 0 : (float)i / steps);
            var shapeGroups = Document.Strokes.Where(s => s.ShapeSource?.Geometry is not null && s.Bounds.Inflate(radius).Contains(p)
                && s.DistanceTo(p) <= radius + s.HitRadius).Select(s => s.ShapeSource.GroupId).ToHashSet();
            if (shapeGroups.Count > 0)
            {
                var shapes = Document.Strokes.Where(s => s.ShapeSource is { } source && shapeGroups.Contains(source.GroupId)).ToList();
                if (!_partialEraseGesture) _erasedThisGesture.AddRange(shapes);
                Document.RemoveAll(shapes); any = true;
            }
            if (_partialEraseGesture)
            {
                var currentStrokes = Document.Strokes.Where(stroke => stroke.Bounds.Inflate(radius).Contains(p)).ToArray();
                foreach (var stroke in currentStrokes)
                {
                    if (!StrokeEraser.EraseDisc(stroke, p, radius, out var remaining)) continue;
                    Document.Replace(stroke, remaining);
                    any = true;
                }
            }
            else
            {
                var hits = Document.EraseAt(p, radius);
                if (hits.Count > 0)
                {
                    _erasedThisGesture.AddRange(hits);
                    any = true;
                }
            }
        }
        if (any) Invalidated?.Invoke();
        return any;
    }

    private void EndErase()
    {
        _erasing = false;
        _gestureEraserRadius = null;
        if (_partialEraseGesture)
        {
            RecordAreaErase();
            _eraseBeforeSnapshot = null;
            _partialEraseGesture = false;
        }
        else if (_erasedThisGesture.Count > 0)
        {
            var removed = _erasedThisGesture.ToList();
            History.Push(new DelegateAction("erase",
                () => Document.RemoveAll(removed),
                () => Document.AddRange(removed)));
            _erasedThisGesture.Clear();
            StrokesErased?.Invoke(removed);
        }
        Invalidated?.Invoke();
    }

    private void RecordAreaErase()
    {
        var before = _eraseBeforeSnapshot;
        if (before is null) return;
        var originals = before.ToHashSet();
        var present = Document.Strokes.ToHashSet();
        var removed = before.Select((stroke, index) => (Index: index, Stroke: stroke))
            .Where(item => !present.Contains(item.Stroke)).ToArray();
        if (removed.Length == 0) return;
        var added = Document.Strokes.Select((stroke, index) => (Index: index, Stroke: stroke))
            .Where(item => !originals.Contains(item.Stroke)).ToArray();
        // Only changed ink belongs to this history entry. Untouched strokes keep their identity.
        var active = added.Select(item => item.Stroke).ToArray();
        History.Push(new DelegateAction("area erase", () => Restore(added), () => Restore(removed)));

        void Restore((int Index, InkStroke Stroke)[] snapshot)
        {
            using (Document.DeferNotifications())
            {
                Document.RemoveAll(active);
                Document.InsertRange(snapshot);
            }
            active = snapshot.Select(item => item.Stroke).ToArray();
            _frameStrokes.Clear(); _frameElements.Clear();
            ClearSelection();
        }
    }

    // ---- selection --------------------------------------------------------

    /// <summary>Axis-aligned selection box in view coordinates, where the handles are drawn.</summary>
    private Rect ViewBoundsOf(Rect worldBounds)
    {
        if (worldBounds.IsEmpty) return Rect.Empty;
        var corners = new[]
        {
            new Vector2(worldBounds.Left, worldBounds.Top),
            new Vector2(worldBounds.Right, worldBounds.Top),
            new Vector2(worldBounds.Right, worldBounds.Bottom),
            new Vector2(worldBounds.Left, worldBounds.Bottom),
        };
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var c in corners)
        {
            var v = Vector2.Transform(c, ViewTransform);
            minX = MathF.Min(minX, v.X); minY = MathF.Min(minY, v.Y);
            maxX = MathF.Max(maxX, v.X); maxY = MathF.Max(maxY, v.Y);
        }
        return Rect.FromLTRB(minX, minY, maxX, maxY);
    }

    private void BeginSelectionInteraction(in OverlayPointer p, Vector2 world)
    {
        var viewPoint = new Vector2(p.X, p.Y);
        _selectionPointerId = p.Id;
        var handle = HasSelection ? SelectionFrame.HitTest(viewPoint, ViewTransform) : SelectionHandle.None;
        if (handle == SelectionHandle.Move && Selection.Select(stroke => stroke.ShapeSource?.GroupId ?? stroke.Id).Distinct().Count() + SelectedElements.Count == 1
            && HitTestDrawingObject(world) is { } top && !IsSelectedObject(top))
            handle = SelectionHandle.None;
        _controlCloneDrag = p.Control && handle == SelectionHandle.Move;
        _controlClickStroke = null; _controlClickElement = null;
        if (_controlCloneDrag)
        {
            _controlClickElement = SelectedElements.LastOrDefault(element => element.Frame.Contains(world, Matrix3x2.Identity));
            if (_controlClickElement is null)
                _controlClickStroke = Document.HitTest(world, 6f / MathF.Max(.1f, MathF.Sqrt(MathF.Abs(ViewTransform.GetDeterminant()))))
                    .LastOrDefault(stroke => Selection.Contains(stroke));
        }
        else if (p.Control) handle = SelectionHandle.None;
        if (handle == SelectionHandle.None)
        {
            var hit = HitTestDrawingObject(world);
            var hitElement = hit?.Element;
            var hitStroke = hit?.Stroke;
            bool additive = p.Control || p.Shift;
            if (hitElement is null && hitStroke is null)
            {
                _marqueeInitialStrokes = additive ? Selection.ToList() : new();
                _marqueeInitialElements = additive ? SelectedElements.ToList() : new();
                if (!additive) { Selection.Clear(); SelectedElements.Clear(); }
                IsMarqueeSelecting = true;
                _dragFromView = viewPoint;
                MarqueeBounds = Rect.Empty;
                SelectionChanged?.Invoke();
                Invalidated?.Invoke();
                return;
            }
            if (!additive) { Selection.Clear(); SelectedElements.Clear(); }
            if (hitElement is not null)
            {
                if (p.Control && SelectedElements.Contains(hitElement)) SelectedElements.Remove(hitElement);
                else if (!SelectedElements.Contains(hitElement))
                {
                    SelectedElements.Add(hitElement);
                    AddSelectionGroupMembers(hitElement);
                }
            }
            if (hitStroke is not null)
            {
                var members = ShapeMembers(hitStroke).ToArray();
                if (p.Control && Selection.Contains(hitStroke)) Selection.RemoveAll(stroke => members.Contains(stroke));
                else foreach (var member in members) if (!Selection.Contains(member)) Selection.Add(member);
            }
            SelectionChanged?.Invoke();
            if (p.Control || !HasSelection) { Invalidated?.Invoke(); return; }
            handle = SelectionHandle.Move;
        }
        _activeHandle = handle;
        _dragFromView = viewPoint;
        _viewAtDragStart = ViewTransform;
        _dragFrame = SelectionFrame;
        _dragBounds = SelectionBounds;
        _dragTargets = Selection.ToList();
        _dragElements = SelectedElements.ToList();
        _cloneDrag = false;
        _dragMoved = false;
        _selectionPreview = Matrix3x2.Identity;
        _previewSet.Clear();
        // Translation/rotation keep the source points intact until release. Capture history
        // then, so pressing or cloning a large selection never duplicates every sample twice.
        _dragSnapshot = null;
        _dragElementSnapshot = _dragElements.Select(e => e.Capture()).ToList();
        Invalidated?.Invoke();
    }

private void EndMarquee(Vector2 endWorld)
    {
        _marqueeing = false;

        var a = _marqueeStartView;
        var b = _marqueeCurrentView;
        float vLeft = MathF.Min(a.X, b.X), vTop = MathF.Min(a.Y, b.Y);
        float vRight = MathF.Max(a.X, b.X), vBottom = MathF.Max(a.Y, b.Y);
        var viewRect = Rect.FromLTRB(vLeft, vTop, vRight, vBottom);

        Selection.Clear();
        if (viewRect.Width >= 6 && viewRect.Height >= 6)
        {
            float wLeft = MathF.Min(_marqueeStartWorld.X, endWorld.X);
            float wTop = MathF.Min(_marqueeStartWorld.Y, endWorld.Y);
            float wRight = MathF.Max(_marqueeStartWorld.X, endWorld.X);
            float wBottom = MathF.Max(_marqueeStartWorld.Y, endWorld.Y);
            var worldRect = Rect.FromLTRB(wLeft, wTop, wRight, wBottom);
            Selection.AddRange(Document.HitTest(worldRect));
        }

        SelectionChanged?.Invoke();
        Invalidated?.Invoke();
    }


    private void UpdateSelectionDrag(Vector2 viewPoint, bool constrain = false, bool control = false)
    {
        if (_dragTargets is null) return;
        if (!_dragMoved && Vector2.DistanceSquared(viewPoint, _dragFromView) < 9) return;
        _dragMoved = true;
        var worldDelta = _dragFrame.Drag(_activeHandle, _dragFromView, viewPoint, _viewAtDragStart, constrain, out _);
        _selectionPreview = worldDelta;
        if ((CloneMode || _controlCloneDrag || control) && _activeHandle == SelectionHandle.Move && !_cloneDrag)
        {
            _controlCloneDrag |= control;
            _dragTargets = InkStroke.CloneObjects(Selection);
            _dragElements = SelectedElements.Select(e => e.Duplicate()).ToList();
            SetCloneLayerOrder();
            _cloneDrag = true;
            _previewSet.Clear();
            _previewSet.UnionWith(_dragTargets);
            Document.AddRange(_dragTargets);
            foreach (var element in _dragElements) element.SetPresent(true);
        }
        if (_previewSet.Count == 0) _previewSet.UnionWith(_dragTargets);
        if (!DeferSelectionPoints)
        {
            _dragSnapshot ??= _dragTargets.Select(stroke => stroke.Clone()).ToList();
            ApplySnapshot(_dragTargets, _dragSnapshot);
            foreach (var stroke in _dragTargets) stroke.Transform(worldDelta);
            Document.Touch();
        }
        for (int i = 0; i < _dragElements.Count; i++)
        {
            _dragElements[i].TransformFrom(_dragElementSnapshot[i], worldDelta);
        }
        Invalidated?.Invoke();
    }

    private void EndSelectionDrag(bool gesture = false)
    {
        var targets = _dragTargets ?? Selection;
        var before = _dragSnapshot;
        bool cloneDrag = _cloneDrag;
        bool controlClone = _controlCloneDrag;
        bool moved = _dragMoved;
        var elements = _dragElements;
        var beforeElements = _dragElementSnapshot;
        var beforeFrame = _dragFrame;
        var afterFrame = SelectionFrame;
        if (moved && !cloneDrag) before ??= targets.Select(stroke => stroke.Clone()).ToList();
        if (moved && (DeferSelectionPoints || gesture)) foreach (var stroke in targets) stroke.Transform(_selectionPreview);
        _activeHandle = SelectionHandle.None;
        _dragSnapshot = null;
        _dragTargets = null;
        _cloneDrag = false;
        _controlCloneDrag = false;
        _dragElements = null; _dragElementSnapshot = null;
        _dragMoved = false;
        _selectionPreview = Matrix3x2.Identity;
        _previewSet.Clear();
        if (!moved && controlClone)
        {
            if (_controlClickStroke is not null) { var members = ShapeMembers(_controlClickStroke).ToHashSet(); Selection.RemoveAll(members.Contains); }
            if (_controlClickElement is not null) SelectedElements.Remove(_controlClickElement);
        }
        _controlClickStroke = null; _controlClickElement = null;
        if (moved)
        {
            if (!cloneDrag) RememberFrame(afterFrame);
            Document.Touch();
        }
        if (cloneDrag && moved)
        {
            // One undo unit: undo removes the copies, redo puts them back where they landed.
            var clones = targets;
            var sourceStrokes = Selection.ToList();
            var sourceElements = SelectedElements.ToList();
            if (controlClone) SelectCopies(true);
            History.Push(new DelegateAction("clone selection",
                () =>
                {
                    Document.AddRange(clones);
                    for (int i = elements.Count - 1; i >= 0; i--) elements[i].SetPresent(true);
                    if (controlClone) SelectCopies(true);
                    Invalidated?.Invoke();
                },
                () =>
                {
                    Document.RemoveAll(clones);
                    foreach (var element in elements) element.SetPresent(false);
                    if (controlClone) SelectCopies(false);
                    Invalidated?.Invoke();
                }));

            void SelectCopies(bool copies)
            {
                if (!Selection.SequenceEqual(copies ? sourceStrokes : clones)
                    || !SelectedElements.SequenceEqual(copies ? sourceElements : elements)) return;
                Selection.Clear(); Selection.AddRange(copies ? clones : sourceStrokes);
                SelectedElements.Clear(); SelectedElements.AddRange(copies ? elements : sourceElements);
                RememberFrame(copies ? afterFrame : beforeFrame);
                SelectionChanged?.Invoke();
            }
        }
        else if (moved && before is not null && before.Count == targets.Count)
        {
            var after = targets.Select(s => s.Clone()).ToList();
            var afterElements = elements.Select(e => e.Capture()).ToList();
            History.Push(new DelegateAction("transform selection",
                () => Restore(after, afterElements, afterFrame),
                () => Restore(before, beforeElements, beforeFrame)));
        }
        SelectionChanged?.Invoke();
        Invalidated?.Invoke();

        void Restore(List<InkStroke> strokeState, List<object> elementState, SelectionFrame frame)
        {
            ApplySnapshot(targets, strokeState);
            for (int i = 0; i < elements.Count; i++) elements[i].Restore(elementState[i]);
            RestoreSelectionFrame(targets, elements, frame);
            Document.Touch(); SelectionChanged?.Invoke(); Invalidated?.Invoke();
        }
    }

    private void UpdateMarquee(Vector2 viewPoint)
    {
        MarqueeBounds = Rect.FromLTRB(MathF.Min(_dragFromView.X, viewPoint.X), MathF.Min(_dragFromView.Y, viewPoint.Y),
            MathF.Max(_dragFromView.X, viewPoint.X), MathF.Max(_dragFromView.Y, viewPoint.Y));
        if (Vector2.DistanceSquared(_dragFromView, viewPoint) >= 9)
        {
            Selection.Clear(); Selection.AddRange(_marqueeInitialStrokes);
            foreach (var stroke in Document.HitTest(MarqueeBounds, ViewTransform)) if (!Selection.Contains(stroke)) Selection.Add(stroke);
            ExpandShapeSelection();
            SelectedElements.Clear(); SelectedElements.AddRange(_marqueeInitialElements);
            if (GetSelectableElements is not null)
                foreach (var element in GetSelectableElements())
                    if (element.IsPresent && element.Frame.Intersects(MarqueeBounds, ViewTransform) && !SelectedElements.Contains(element)) SelectedElements.Add(element);
            SelectionChanged?.Invoke();
        }
        Invalidated?.Invoke();
    }

    private static void ApplySnapshot(List<InkStroke> targets, List<InkStroke> snapshot)
    {
        for (int i = 0; i < targets.Count && i < snapshot.Count; i++)
        {
            targets[i].RestorePoints(snapshot[i].Points);
            targets[i].Attributes.BaseWidth = snapshot[i].Attributes.BaseWidth;
            targets[i].Attributes.NibRotation = snapshot[i].Attributes.NibRotation;
            targets[i].Attributes.InkWash = snapshot[i].Attributes.InkWash;
            targets[i].ShapeSource = snapshot[i].ShapeSource;
            targets[i].ObjectFrame = snapshot[i].ObjectFrame;
        }
    }

    public void BeginSelectionGesture()
    {
        if (_selectionGesture || !HasSelection) return;
        if (_activeHandle != SelectionHandle.None)
        {
            bool deferred = DeferSelectionPoints;
            _selectionGesture = true;
            _activeHandle = SelectionHandle.None;
            if (_dragMoved && !deferred && _dragSnapshot is not null)
            {
                ApplySnapshot(_dragTargets, _dragSnapshot);
                Document.Touch();
            }
            return;
        }
        IsMarqueeSelecting = false; MarqueeBounds = Rect.Empty;
        _marqueeInitialStrokes = null; _marqueeInitialElements = null;
        _dragTargets = Selection.ToList(); _dragSnapshot = null;
        _dragElements = SelectedElements.ToList(); _dragElementSnapshot = _dragElements.Select(e => e.Capture()).ToList();
        _dragFrame = SelectionFrame; _cloneDrag = false; _dragMoved = false;
        _dragBounds = SelectionBounds;
        _controlCloneDrag = false; _controlClickStroke = null; _controlClickElement = null;
        _selectionPreview = Matrix3x2.Identity; _previewSet.Clear();
        _selectionGesture = true;
    }

    public void ApplySelectionGesture(Matrix3x2 delta)
    {
        if (delta == Matrix3x2.Identity) return;
        SetSelectionGestureTransform(_selectionPreview * delta);
    }

    private void SetSelectionGestureTransform(Matrix3x2 transform)
    {
        if (transform == _selectionPreview || _dragTargets is null) return;
        _selectionPreview = transform;
        if (CloneMode && !_cloneDrag)
        {
            _dragTargets = InkStroke.CloneObjects(Selection);
            _dragElements = SelectedElements.Select(e => e.Duplicate()).ToList();
            SetCloneLayerOrder();
            _cloneDrag = true;
            _previewSet.Clear();
            _previewSet.UnionWith(_dragTargets);
            _dragMoved = true;
            Document.AddRange(_dragTargets);
            foreach (var element in _dragElements) element.SetPresent(true);
        }
        _dragMoved = true;
        if (_previewSet.Count == 0) _previewSet.UnionWith(_dragTargets);
        for (int i = 0; i < _dragElements.Count; i++)
            _dragElements[i].TransformFrom(_dragElementSnapshot[i], _selectionPreview);
        Invalidated?.Invoke();
    }

    public void EndSelectionGesture()
    {
        if (!_selectionGesture) return;
        _selectionGesture = false;
        _selectionTouchPair = null;
        _selectionManipulator.Reset();
        EndSelectionDrag(gesture: true);
    }

    /// <summary>Apply a gesture matrix to the selection, recording one undoable step.</summary>
    private void TransformSelectionByGesture(Matrix3x2 worldDelta)
    {
        var targets = Selection.ToList();
        var before = targets.Select(s => s.Clone()).ToList();
        foreach (var s in targets) s.Transform(worldDelta);
        var after = targets.Select(s => s.Clone()).ToList();
        History.Push(new DelegateAction("gesture selection",
            () => { ApplySnapshot(targets, after); Document.Touch(); Invalidated?.Invoke(); },
            () => { ApplySnapshot(targets, before); Document.Touch(); Invalidated?.Invoke(); }));
        Document.Touch();
    }

    public void DeleteSelection()
    {
        if (_selectionGesture) EndSelectionGesture();
        else if (_activeHandle != SelectionHandle.None) EndSelectionDrag();
        if (!HasSelection) return;
        var removed = Selection.ToList();
        var elements = SelectedElements.ToList();
        Document.RemoveAll(removed);
        foreach (var e in elements) e.SetPresent(false);
        History.Push(new DelegateAction("delete",
            () => { Document.RemoveAll(removed); foreach (var e in elements) e.SetPresent(false); },
            () => { Document.AddRange(removed); for (int i = elements.Count - 1; i >= 0; i--) elements[i].SetPresent(true); }));
        ClearSelection();
    }

    public void TransformSelection(Matrix3x2 m)
    {
        if (Selection.Count == 0) return;
        InkDocument.Transform(Selection, m);
        Invalidated?.Invoke();
    }

    public void TransformSelectionUndoable(Matrix3x2 matrix, string name, bool scaleWidth = true)
    {
        if (!HasSelection) return;
        var strokes = Selection.ToList();
        var elements = SelectedElements.ToList();
        var beforeFrame = SelectionFrame;
        var before = strokes.Select(s => s.Clone()).ToList();
        var beforeElements = elements.Select(e => e.Capture()).ToList();
        foreach (var stroke in strokes) stroke.Transform(matrix, scaleWidth);
        foreach (var element in elements) element.Transform(matrix);
        var after = strokes.Select(s => s.Clone()).ToList();
        var afterElements = elements.Select(e => e.Capture()).ToList();
        var afterFrame = beforeFrame with { Transform = beforeFrame.Transform * matrix };
        RememberFrame(afterFrame);
        History.Push(new DelegateAction(name, () => Restore(after, afterElements, afterFrame),
            () => Restore(before, beforeElements, beforeFrame)));
        Document.Touch(); SelectionChanged?.Invoke(); Invalidated?.Invoke();

        void Restore(List<InkStroke> state, List<object> elementState, SelectionFrame frame)
        {
            ApplySnapshot(strokes, state);
            for (int i = 0; i < elements.Count; i++) elements[i].Restore(elementState[i]);
            RestoreSelectionFrame(strokes, elements, frame); Document.Touch(); SelectionChanged?.Invoke(); Invalidated?.Invoke();
        }
    }

    // ---- commands ---------------------------------------------------------

    public void RecolorSelection(InkColor color)
        => ChangeSelectionAttributes("selection color", attributes => attributes.Color = color);

    public void ScaleSelectionWidth(float factor)
        => ChangeSelectionAttributes("selection width", attributes => attributes.BaseWidth = Math.Clamp(attributes.BaseWidth * factor, .5f,
            attributes.EffectiveTaper == TaperKind.InkWash ? 120 : 60));

    public void RestoreSelectionWidth()
        => ChangeSelectionAttributes("selection width restore", attributes => attributes.BaseWidth = attributes.EffectiveTaper == TaperKind.InkWash
            ? InkWashWidth : CurrentAttributes.BaseWidth);

    private void ChangeSelectionAttributes(string name, Action<StrokeAttributes> change)
    {
        var targets = Selection.Where(s => s.ShapeSource?.Geometry?.IsFill != true).ToArray();
        if (targets.Length == 0) return;
        var before = targets.Select(stroke => stroke.Attributes.Clone()).ToArray();
        foreach (var stroke in targets)
        {
            float width = stroke.Attributes.BaseWidth;
            change(stroke.Attributes);
            AdjustShapeWidth(stroke, width);
        }
        var after = targets.Select(stroke => stroke.Attributes.Clone()).ToArray();
        History.Push(new DelegateAction(name, () => Restore(after), () => Restore(before)));
        Document.Touch(); SelectionChanged?.Invoke(); Invalidated?.Invoke();

        void Restore(StrokeAttributes[] attributes)
        {
            for (int i = 0; i < targets.Length; i++)
            {
                float width = targets[i].Attributes.BaseWidth;
                targets[i].Attributes.Color = attributes[i].Color;
                targets[i].Attributes.BaseWidth = attributes[i].BaseWidth;
                AdjustShapeWidth(targets[i], width);
            }
            Document.Touch(); SelectionChanged?.Invoke(); Invalidated?.Invoke();
        }
    }

    private static void AdjustShapeWidth(InkStroke stroke, float oldWidth)
    {
        if (stroke.ShapeSource?.Geometry is null || oldWidth <= 0 || oldWidth == stroke.Attributes.BaseWidth) return;
        float ratio = stroke.Attributes.BaseWidth / oldWidth;
        var points = stroke.Points.Select(p => { p.Width *= ratio; return p; }).ToArray();
        var frame = stroke.ObjectFrame;
        stroke.RestorePoints(points); stroke.ObjectFrame = frame;
    }

    public void Undo()
    {
        EndPlayback();
        CancelLassoErase();
        if (History.Undo()) { PruneSelection(); Invalidated?.Invoke(); }
    }

    public void Redo()
    {
        EndPlayback();
        CancelLassoErase();
        if (History.Redo()) { PruneSelection(); Invalidated?.Invoke(); }
    }

    private void PruneSelection()
    {
        int removed = Selection.RemoveAll(s => !Document.Strokes.Contains(s));
        removed += SelectedElements.RemoveAll(e => !e.IsPresent);
        if (removed > 0) SelectionChanged?.Invoke();
    }

    public void Clear()
    {
        CancelLassoErase();
        ClearSelection();
        var elements = GetSelectableElements?.Invoke().Where(element => element.IsPresent).ToList() ?? new();
        if (Document.Count == 0 && elements.Count == 0) return;
        var removed = Document.Strokes.ToList();
        Document.Clear();
        foreach (var element in elements) element.SetPresent(false);
        History.Push(new DelegateAction("clear",
            () => { Document.RemoveAll(removed); foreach (var element in elements) element.SetPresent(false); },
            () => { Document.AddRange(removed); for (int i = elements.Count - 1; i >= 0; i--) elements[i].SetPresent(true); }));
        Cleared?.Invoke();
        Invalidated?.Invoke();
    }

    private void CancelTouchInk(long surface)
    {
        // Allocation-free: reuse a scratch list instead of LINQ Where(...).ToList() per gesture.
        _scratchIds.Clear();
        foreach (var id in _live.Keys)
            if (_touches.TryGetValue(id, out var touch) && touch.InputSurface == surface) _scratchIds.Add(id);
        foreach (var id in _scratchIds)
        {
            _live.Remove(id);
            _strokeStartTimes.Remove(id);
            _lineEndpoints.Remove(id);
            RemoveShapeHold(id);
        }
    }

    /// <summary>
    /// First two active touch contacts in enumeration order (what <c>_touches.Values.Take(2)</c>
    /// produced), without allocating an array or LINQ iterators on the per-move gesture path.
    /// </summary>
    private bool TryGetTouchPair(out OverlayPointer a, out OverlayPointer b, long? surface = null)
    {
        a = default; b = default;
        foreach (var first in _touches.Values)
        {
            if (surface.HasValue && first.InputSurface != surface.Value) continue;
            foreach (var second in _touches.Values)
                if (first.Id != second.Id && first.InputSurface == second.InputSurface)
                { a = first; b = second; return true; }
        }
        return false;
    }

    public Vector2 ToWorld(in OverlayPointer p)
    {
        Matrix3x2.Invert(ViewTransform, out var inverse);
        return Vector2.Transform(new Vector2(p.X, p.Y), inverse);
    }

    public void Dispose()
    {
        EndPlayback(); Document.Changed -= OnPlaybackDocumentChanged;
        _panPointerId = null;
        _lassoPath.Clear(); IsLassoErasing = false;
        _live.Clear();
        ClearShapeHolds();
        _dotAnimations.Clear();
        _strokeStartTimes.Clear();
        _eraserPreviewPointer = null;
        _lineEndpoints.Clear();
        _touches.Clear();
        _suppressedTouches.Clear();
        Selection.Clear();
        SelectedElements.Clear(); GetSelectableElements = null;
        _frameStrokes.Clear(); _frameElements.Clear();
        _shapeDragStart.Clear();
        _stepPending.Clear();
        _shapePreview.Clear();
        _eraseBeforeSnapshot = null;
        _erasedThisGesture.Clear();
        _drawingObjects = Array.Empty<DrawingObject>();
        History.Clear();
    }
}
