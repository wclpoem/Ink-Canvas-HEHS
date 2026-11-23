using InkCanvas.Core.AiAssistant;
using InkCanvas.Core.History;
using InkCanvas.Core.Ink;
using InkCanvas.Features.AiAssistant;
using InkCanvas.Features.Board.AiObjects;
using InkCanvas.Ink;
using System.Numerics;
using System.Text.Json;

namespace InkCanvas.Features.Board;

public enum BoardBackground { Black, White, Grid, Lined, Green, Cream, Coordinate }

/// <summary>Lightweight page summary for thumbnail/list UIs (no bitmap rendering required).</summary>
public readonly record struct BoardPageThumbnail(
    int Index, string Name, BoardBackground Background, int StrokeCount, int ImageCount);

public sealed record BoardPageInfo(InkController Controller, BoardBackground Background, float GridSpacing,
    IReadOnlyList<BoardImageAsset> Images, IReadOnlyList<BoardMediaAsset> Media,
    IReadOnlyList<BoardTextObject> TextObjects = null, IReadOnlyList<BoardFormulaObject> FormulaObjects = null);

/// <summary>
/// AI 板书对象变化：供课堂录制器写 <c>TimelineEventKind.AiStep</c>，与 <c>ImagesChanged</c>
/// 分离，避免普通图片变化污染时光机对象层。
/// </summary>
public sealed class BoardAiObjectChange
{
    /// <summary>add / replace / update / remove。</summary>
    public string Action { get; init; } = "";

    public int PageIndex { get; init; }

    /// <summary>本次变化涉及的图片对象快照；remove 时用于保留被删除对象。</summary>
    public IReadOnlyList<BoardImageAsset> Objects { get; init; } = Array.Empty<BoardImageAsset>();

    /// <summary>本次变化涉及的原生文本对象快照。</summary>
    public IReadOnlyList<BoardTextObject> Texts { get; init; } = Array.Empty<BoardTextObject>();

    /// <summary>本次变化涉及的原生公式对象快照。</summary>
    public IReadOnlyList<BoardFormulaObject> Formulas { get; init; } = Array.Empty<BoardFormulaObject>();

    /// <summary>replace 时被删除的笔画；录制器据此补写既有 StrokesRemoved 事件。</summary>
    public IReadOnlyList<InkStroke> RemovedStrokes { get; init; } = Array.Empty<InkStroke>();
}

/// <summary>A bitmap or SVG image placed on a board page; position and size are device-independent pixels.</summary>
public sealed class BoardImageAsset
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public long? LayerOrder { get; set; }
    public string Name { get; set; } = "Image";
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public string Metadata { get; set; } = "";
    public string ContentType { get; set; } = "image/png";
    public float Left { get; set; }
    public float Top { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public float Opacity { get; set; } = 1;

    /// <summary>统一字符串分组标识（与原生文本/公式共用）；空串表示未分组。</summary>
    public string GroupId { get; set; } = "";

    /// <summary>对象结构版本；旧文件缺字段时按 1 读取。</summary>
    public int Version { get; set; } = 1;


    /// <summary>
    /// Cumulative canvas-space transform, the port of the WPF element RenderTransform
    /// TransformGroup (MW_SelectionGestures.cs ApplyElementMatrixTransform). Six row-major
    /// floats (M11,M12,M21,M22,M31,M32); null means identity. Appended, never replaced, so
    /// rotate/flip/gesture deltas compose exactly like the original TransformGroup.Children.Add.
    /// </summary>
    public float[] Transform { get; set; }

    public BoardImageAsset Copy(bool preserveId = true, bool copyData = true) => new()
    {
        Id = preserveId ? Id : Guid.NewGuid(),
        LayerOrder = LayerOrder,
        Name = Name,
        Data = copyData ? Data.ToArray() : Data,
        ContentType = ContentType,
        Metadata = Metadata,
        Left = Left,
        Top = Top,
        Width = Width,
        Height = Height,

        Opacity = Opacity,
        GroupId = GroupId,
        Version = Version,
        Transform = Transform?.ToArray(),
    };
}

public readonly record struct BoardImageGeometry(float Left, float Top, float Width, float Height);


/// <summary>A video file embedded on a whiteboard page, with DIP placement/size.</summary>
public sealed class BoardMediaAsset
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public long? LayerOrder { get; set; }
    public string Name { get; set; } = "Video";
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public byte[] PosterData { get; set; } = Array.Empty<byte>();
    public uint NaturalWidth { get; set; }
    public uint NaturalHeight { get; set; }
    public string ContentType { get; set; } = "video/mp4";
    public float Left { get; set; }
    public float Top { get; set; }
    public float Width { get; set; } = 360;
    public float Height { get; set; } = 240;

    /// <summary>统一字符串分组标识（与原生文本/公式共用）；空串表示未分组。</summary>
    public string GroupId { get; set; } = "";

    /// <summary>对象结构版本；旧文件缺字段时按 1 读取。</summary>
    public int Version { get; set; } = 1;

    /// <inheritdoc cref="BoardImageAsset.Transform"/>
    public float[] Transform { get; set; }

    public BoardMediaAsset Copy(bool preserveId = true, bool copyData = true) => new()
    {
        Id = preserveId ? Id : Guid.NewGuid(), LayerOrder = LayerOrder, Name = Name, Data = copyData ? Data.ToArray() : Data, ContentType = ContentType,
        PosterData = copyData ? PosterData.ToArray() : PosterData, NaturalWidth = NaturalWidth, NaturalHeight = NaturalHeight,
        Left = Left, Top = Top, Width = Width, Height = Height,
        GroupId = GroupId, Version = Version,
        Transform = Transform?.ToArray(),
    };

    public MemoryStream OpenRead() => new(Data, 0, Data.Length, writable: false, publiclyVisible: true);
}

public readonly record struct BoardMediaGeometry(float Left, float Top, float Width, float Height);

/// <summary>
/// Math for the per-element canvas-space transform (WPF RenderTransform TransformGroup,
/// MW_SelectionGestures.cs ApplyElementMatrixTransform / MW_ElementsControls.cs CenterAndScaleElement).
/// Matrices use the row-vector convention shared by WPF and System.Numerics: p' = p * M.
/// </summary>
public static class BoardElementMath
{
    public static Matrix3x2 Read(float[] transform)
        => transform is { Length: 6 }
            ? new Matrix3x2(transform[0], transform[1], transform[2], transform[3], transform[4], transform[5])
            : Matrix3x2.Identity;

    public static float[] Write(Matrix3x2 m)
        => new[] { m.M11, m.M12, m.M21, m.M22, m.M31, m.M32 };

    /// <summary>WPF TransformGroup.Children.Add: the new matrix acts after the existing ones.</summary>
    public static float[] Append(float[] existing, Matrix3x2 m)
        => Write(Read(existing) * m);

    public static bool Same(float[] a, float[] b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > 1e-4f) return false;
        return true;
    }

    /// <summary>Axis-aligned hull of the transformed element rectangle, in canvas DIPs.</summary>
    public static Rect TransformedBounds(float left, float top, float width, float height, float[] transform)
    {
        var m = Read(transform);
        var corners = new[]
        {
            new Vector2(left, top),
            new Vector2(left + width, top),
            new Vector2(left + width, top + height),
            new Vector2(left, top + height),
        };
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var c in corners)
        {
            var v = Vector2.Transform(c, m);
            minX = MathF.Min(minX, v.X); minY = MathF.Min(minY, v.Y);
            maxX = MathF.Max(maxX, v.X); maxY = MathF.Max(maxY, v.Y);
        }
        return Rect.FromLTRB(minX, minY, maxX, maxY);
    }

    /// <summary>
    /// The WinUI RenderTransform (origin 0,0) that reproduces the canvas-space matrix for a view
    /// placed at Canvas.Left/Top = (left,top): final = M * (p_local + t), so the local matrix keeps
    /// M's linear part and offsets by M*t - t.
    /// </summary>
    public static Matrix3x2 RenderMatrix(float left, float top, float[] transform)
    {
        var m = Read(transform);
        var t = new Vector2(left, top);
        var offset = Vector2.Transform(t, m) - t;
        return new Matrix3x2(m.M11, m.M12, m.M21, m.M22, offset.X, offset.Y);
    }
}

/// <summary>
/// Multi-page blackboard/whiteboard. Every page owns its ink controller, undo history and image
/// collection. Coordinates and sizes in both collections are stored in WinUI device-independent
/// pixels, not physical screen pixels.
/// </summary>
public sealed partial class BoardService
{
    private const string BoardFormat = "inkcanvas-board";
    private const int BoardFormatVersion = 4;
    private const int SideBoardFormatVersion = 1;
    public const int MaximumPages = 99;
    private const float MinimumGridSpacing = 12f;
    private const float MaximumGridSpacing = 160f;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly List<InkController> _pages = new();

    /// <summary>与 <see cref="_pages"/> 并行的页身份列表，用于写回时校验同索引换页 ≠ 同页。</summary>
    private readonly List<Guid> _pageIds = new();

    private readonly UndoStack _pageHistory = new(100);
    private int _nextPageNumber = 2;
    private sealed record BoardPaper(BoardBackground Background = BoardBackground.Black, float GridSpacing = 40f, string Name = null);
    private readonly Dictionary<InkController, BoardPaper> _papers = new();
    private readonly Dictionary<Guid, BoardSelectionElement> _selectionElements = new();
    private readonly List<List<BoardImageAsset>> _images = new() { new List<BoardImageAsset>() };
    private readonly List<BoardPageState> _states = new() { new BoardPageState { Name = "页面 1" } };
    private readonly List<List<BoardMediaAsset>> _media = new() { new List<BoardMediaAsset>() };

    /// <summary>Native AI text objects, parallel to <see cref="_pages"/>; never baked images.</summary>
    private readonly List<List<BoardTextObject>> _textObjects = new() { new List<BoardTextObject>() };

    /// <summary>Native formula objects, parallel to <see cref="_pages"/>; LaTeX is persisted.</summary>
    private readonly List<List<BoardFormulaObject>> _formulaObjects = new() { new List<BoardFormulaObject>() };

    public BoardService(InkController initialController = null)
    {
        var page = initialController ?? new InkController();
        _pages.Add(page);
        _pageIds.Add(Guid.NewGuid());
        _papers.Add(page, new(Name: "页面 1"));
        BindPage(page);
        ImagesChanged += () => Current.NotifyElementsChanged();
    }

    /// <summary>
    /// 绑定页面控制器：元素选择源 + 元素删除/恢复事件。删除事件统一转成板级对象变更，
    /// 让时间轴录制（AiStep remove/add）与图片变更共用同一事件源。
    /// </summary>
    private void BindPage(InkController page)
    {
        if (page is null) return;
        page.GetSelectableElements = () => GetSelectionElements(page);
        page.SelectionElementsPresenceChanged += (elements, present)
            => NotifySelectionElementsPresenceChanged(page, elements, present);
    }

    /// <summary>
    /// 选区元素被删除/恢复后，把可录制的对象（图片、文本、公式）汇总成一次对象变更。
    /// 媒体没有 AiStep 载荷，仅触发图片变更刷新；笔画仍走录制器的 id 差异推断。
    /// </summary>
    private void NotifySelectionElementsPresenceChanged(
        InkController page, IReadOnlyList<ISelectionElement> elements, bool present)
    {
        if (elements is null || elements.Count == 0) return;
        int index = _pages.IndexOf(page);
        if (index < 0) return;

        var images = new List<BoardImageAsset>();
        var texts = new List<BoardTextObject>();
        var formulas = new List<BoardFormulaObject>();
        foreach (ISelectionElement element in elements)
        {
            switch (element)
            {
                case BoardSelectionElement selection when selection.Image is not null:
                    images.Add(selection.Image);
                    break;
                case BoardTextObject text:
                    texts.Add(text);
                    break;
                case BoardFormulaObject formula:
                    formulas.Add(formula);
                    break;
            }
        }

        ImagesChanged?.Invoke();
        if (images.Count + texts.Count + formulas.Count == 0) return;
        RaiseAiObjectsChanged(present ? "add" : "remove", index, images, null, texts, formulas);
    }

    public IEnumerable<ISelectionElement> GetSelectionElements() => GetSelectionElements(Current);

    private IEnumerable<ISelectionElement> GetSelectionElements(InkController page)
    {
        int index = _pages.IndexOf(page);
        if (index < 0) yield break;
        var images = _images[index];
        var mediaObjects = _media[index];
        var present = images.Select(image => image.Id).Concat(mediaObjects.Select(media => media.Id)).ToHashSet();
        foreach (var id in _selectionElements.Where(pair => ReferenceEquals(pair.Value.Page, page) && !present.Contains(pair.Key))
            .Select(pair => pair.Key).ToArray()) _selectionElements.Remove(id);
        foreach (var image in images)
        {
            if (!_selectionElements.TryGetValue(image.Id, out var element) || !ReferenceEquals(element.Image, image))
                _selectionElements[image.Id] = element = new BoardSelectionElement(this, image, page);
            yield return element;
        }
        foreach (var media in mediaObjects)
        {
            if (!_selectionElements.TryGetValue(media.Id, out var element) || !ReferenceEquals(element.Media, media))
                _selectionElements[media.Id] = element = new BoardSelectionElement(this, media, page);
            yield return element;
        }
        foreach (var text in _textObjects[index])
        {
            text.Attach(this, page);
            yield return text;
        }
        foreach (var formula in _formulaObjects[index])
        {
            formula.Attach(this, page);
            yield return formula;
        }
    }

    /// <summary>Background of the current page. Kept for backward compatibility with callers.</summary>
    public BoardBackground Background
    {
        get => CurrentState.Background;
        set => SetBackground(value);
    }

    /// <summary>Grid/lined/coordinate spacing of the current page, in device-independent pixels.</summary>
    public float GridSpacing
    {
        get => CurrentState.GridSpacing;
        set
        {
            if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            value = Math.Clamp(value, MinimumGridSpacing, MaximumGridSpacing);
            if (CurrentState.GridSpacing == value) return;
            CurrentState.GridSpacing = value;
            SyncPaper(CurrentIndex);
            BackgroundChanged?.Invoke();
        }
    }

    public int CurrentIndex { get; private set; }

    public int PageCount => _pages.Count;
    public long PageStructureVersion { get; private set; }

    /// <summary>
    /// 页身份，用于写回时校验同索引换页 ≠ 同页；缺失时进程内生成。
    /// 越界返回 <see cref="Guid.Empty"/>，否则返回该索引页面的稳定 Id。
    /// </summary>
    public Guid ResolvePageId(int pageIndex)
        => pageIndex >= 0 && pageIndex < _pageIds.Count ? _pageIds[pageIndex] : Guid.Empty;

    /// <summary>页身份，用于写回时校验同索引换页 ≠ 同页；缺失时进程内生成。</summary>
    public Guid CurrentPageId => ResolvePageId(CurrentIndex);

    public BoardPageInfo GetPageInfo(int index)
    {
        var page = _pages[index]; var paper = _papers[page];
        return new(page, paper.Background, paper.GridSpacing, _images[index], _media[index],
            _textObjects[index], _formulaObjects[index]);
    }

    /// <summary>Page creation is limited only by the runtime collection capacity; <see cref="MaximumPages"/> is a UI soft limit.</summary>
    public bool CanAddPages(int count = 1) => count > 0 && count <= Array.MaxLength - PageCount;

    public bool HasContent => _extensions.Count > 0 || _pages.Any(page => page.Document.Count > 0)
        || _images.Any(images => images.Count > 0)
        || _media.Any(media => media.Count > 0)
        || _textObjects.Any(texts => texts.Count > 0)
        || _formulaObjects.Any(formulas => formulas.Count > 0);

    /// <summary>指定页是否有可离屏渲染的内容（笔画/图片/媒体/原生文本/公式）；越界返回 false。</summary>
    public bool PageHasContent(int index)
        => index >= 0 && index < _pages.Count
           && (_pages[index].Document.Count > 0 || _images[index].Count > 0 || _media[index].Count > 0
               || _textObjects[index].Count > 0 || _formulaObjects[index].Count > 0);

    /// <summary>当前页是否有可渲染贴回的内容（有墨迹，或含有效数据的图片；视频不参与离线 PNG 合成）。</summary>
    public bool CurrentPageHasPasteableContent => Current.Document.Count > 0
        || _images[CurrentIndex].Any(image => image.Data is { Length: > 0 });

    public InkController Current => _pages[CurrentIndex];

    public IReadOnlyList<InkController> Pages => _pages;

/// <summary>与主展板一起保存/加载的板中板服务；为 null 时主文件不写 SideBoard 节点。</summary>
    public BoardService SideBoardSource { get; set; }

    /// <summary>Visual state (name/background/grid/view) of the current page.</summary>
    public BoardPageState CurrentState => _states[CurrentIndex];


    public IReadOnlyList<BoardImageAsset> CurrentImages => _images[CurrentIndex];

    /// <summary>Native AI text objects on the current page (editable, vector-rendered).</summary>
    public IReadOnlyList<BoardTextObject> CurrentTextObjects => _textObjects[CurrentIndex];

    /// <summary>Native formula objects on the current page (LaTeX source retained).</summary>
    public IReadOnlyList<BoardFormulaObject> CurrentFormulaObjects => _formulaObjects[CurrentIndex];

    public bool CanUndoPage => _pageHistory.CanUndo;

    public bool CanRedoPage => _pageHistory.CanRedo;

    public IReadOnlyList<BoardMediaAsset> CurrentMedia => _media[CurrentIndex];
    public event Action<BoardImageAsset> ImagePlacementChanged;
    public event Action<BoardMediaAsset> MediaPlacementChanged;
    public event Action<BoardTextObject> TextPlacementChanged;
    public event Action<BoardFormulaObject> FormulaPlacementChanged;
    public void NotifyImagePlacement(BoardImageAsset image) => ImagePlacementChanged?.Invoke(image);
    public void ReplaceCurrentImages(IEnumerable<BoardImageAsset> images)
    {
        // Insertion/deletion undo actions retain this list. Keep its identity when a screen page changes.
        var incoming = images.ToArray();
        _images[CurrentIndex].Clear();
        _images[CurrentIndex].AddRange(incoming);
        _selectionElements.Clear();
        ImagesChanged?.Invoke();
    }
    public void NotifyMediaPlacement(BoardMediaAsset media) => MediaPlacementChanged?.Invoke(media);

    public void NotifyTextPlacement(BoardTextObject text)
    {
        TextPlacementChanged?.Invoke(text);
        ImagesChanged?.Invoke();
    }

    public void NotifyFormulaPlacement(BoardFormulaObject formula)
    {
        FormulaPlacementChanged?.Invoke(formula);
        ImagesChanged?.Invoke();
    }

    /// <summary>Selection transactions own their undo step; these change membership without nesting history.</summary>
    public int GetImageIndex(InkController page, BoardImageAsset image)
    {
        int index = _pages.IndexOf(page);
        return index < 0 ? -1 : _images[index].IndexOf(image);
    }

    public int GetMediaIndex(InkController page, BoardMediaAsset media)
    {
        int index = _pages.IndexOf(page);
        return index < 0 ? -1 : _media[index].IndexOf(media);
    }

    public int GetTextIndex(InkController page, BoardTextObject text)
    {
        int index = _pages.IndexOf(page);
        return index < 0 || text is null ? -1 : _textObjects[index].IndexOf(text);
    }

    public int GetFormulaIndex(InkController page, BoardFormulaObject formula)
    {
        int index = _pages.IndexOf(page);
        return index < 0 || formula is null ? -1 : _formulaObjects[index].IndexOf(formula);
    }

    public void SetImagePresent(InkController page, BoardImageAsset image, bool present, int restoreIndex = -1)
    {
        int index = _pages.IndexOf(page);
        if (index < 0) return;
        if (present)
        {
            if (!_images[index].Contains(image))
                _images[index].Insert(restoreIndex < 0 ? _images[index].Count : Math.Clamp(restoreIndex, 0, _images[index].Count), image);
        }
        else _images[index].Remove(image);
        if (index == CurrentIndex) ImagesChanged?.Invoke();
    }

    public void SetMediaPresent(InkController page, BoardMediaAsset media, bool present, int restoreIndex = -1)
    {
        int index = _pages.IndexOf(page);
        if (index < 0) return;
        if (present)
        {
            if (!_media[index].Contains(media))
                _media[index].Insert(restoreIndex < 0 ? _media[index].Count : Math.Clamp(restoreIndex, 0, _media[index].Count), media);
        }
        else _media[index].Remove(media);
        if (index == CurrentIndex) ImagesChanged?.Invoke();
    }


    public void SetTextPresent(InkController page, BoardTextObject text, bool present, int restoreIndex = -1)
    {
        int index = _pages.IndexOf(page);
        if (index < 0 || text is null) return;
        if (present)
        {
            if (!_textObjects[index].Contains(text))
                _textObjects[index].Insert(restoreIndex < 0 ? _textObjects[index].Count
                    : Math.Clamp(restoreIndex, 0, _textObjects[index].Count), text);
            text.Attach(this, page);
        }
        else _textObjects[index].Remove(text);
        if (index == CurrentIndex) ImagesChanged?.Invoke();
    }

    public void SetFormulaPresent(InkController page, BoardFormulaObject formula, bool present, int restoreIndex = -1)
    {
        int index = _pages.IndexOf(page);
        if (index < 0 || formula is null) return;
        if (present)
        {
            if (!_formulaObjects[index].Contains(formula))
                _formulaObjects[index].Insert(restoreIndex < 0 ? _formulaObjects[index].Count
                    : Math.Clamp(restoreIndex, 0, _formulaObjects[index].Count), formula);
            formula.Attach(this, page);
        }
        else _formulaObjects[index].Remove(formula);
        if (index == CurrentIndex) ImagesChanged?.Invoke();
    }


    public event Action PageChanged;

    public event Action BackgroundChanged;

    public event Action ImagesChanged;

/// <summary>AI 板书对象增删改（AI 卡片带 <see cref="AiPasteMetadata"/>）；普通图片不触发。</summary>
    public event Action<BoardAiObjectChange> AiObjectsChanged;

    public bool UndoPage() => _pageHistory.Undo();

    public bool RedoPage() => _pageHistory.Redo();

    public void SetBackground(BoardBackground background)
    {
        if (!Enum.IsDefined(background)) throw new ArgumentOutOfRangeException(nameof(background));
        if (CurrentState.Background == background) return;
        CurrentState.Background = background;
        SyncPaper(CurrentIndex);
        BackgroundChanged?.Invoke();

    }

    public void NextPage()
    {
        if (CurrentIndex < _pages.Count - 1)
        {
            GoTo(CurrentIndex + 1);
            return;
        }
        if (!CanAddPages()) return;
        SaveCurrentViewState();
        var source = Current;
        AddPageAt(_pages.Count);
        CurrentIndex = _pages.Count - 1;
        CarryDrawingState(source, Current);
        ApplyCurrentViewState();
        PageChanged?.Invoke();
    }

    public void PreviousPage()
    {
        if (CurrentIndex <= 0) return;
        GoTo(CurrentIndex - 1);
    }

    public void GoTo(int index)
    {
        if (index < 0 || index >= _pages.Count || index == CurrentIndex) return;
        SaveCurrentViewState();
        var source = Current;
        CurrentIndex = index;
        CarryDrawingState(source, Current);
        ApplyCurrentViewState();
        PageChanged?.Invoke();
    }

    public bool InsertPageAfterCurrent()
    {
        if (!CanAddPages()) return false;
        SaveCurrentViewState();
        var source = Current;
        AddPageAt(CurrentIndex + 1);
        CurrentIndex++;
        CarryDrawingState(source, Current);
        ApplyCurrentViewState();
        PageChanged?.Invoke();
        return true;
    }

/// <summary>Deep-copy the current page (strokes, images and visual state) into a page right after it.</summary>
    public bool CopyCurrentPage()
    {
        if (!CanAddPages()) return false;
        SaveCurrentViewState();

        int source = CurrentIndex;
        var sourceState = _states[source];

        var controller = new InkController();
        var strokeCopies = _pages[source].Document.Strokes
            .Select(stroke => FromStrokeFile(ToStrokeFile(stroke)))
            .ToList();
        controller.Document.AddRange(strokeCopies);
        controller.ViewTransform = sourceState.ViewTransform;

        var state = new BoardPageState
        {
            Name = $"{sourceState.Name} 副本",
            Background = sourceState.Background,
            GridSpacing = sourceState.GridSpacing,
            ViewTransform = sourceState.ViewTransform,
        };
        var images = _images[source].Select(image => image.Copy()).ToList();
        var media = _media[source].Select(item => item.Copy(preserveId: false, copyData: false)).ToList();
        var texts = _textObjects[source].Select(text => text.Copy(preserveId: false)).ToList();
        var formulas = _formulaObjects[source].Select(formula => formula.Copy(preserveId: false)).ToList();
        var paper = new BoardPaper(sourceState.Background, sourceState.GridSpacing);

        int insertAt = source + 1;
        Guid copiedPageId = Guid.NewGuid();
        BindPage(controller);
        foreach (BoardTextObject text in texts) text.Attach(this, controller);
        foreach (BoardFormulaObject formula in formulas) formula.Attach(this, controller);
        _pages.Insert(insertAt, controller);
        _pageIds.Insert(insertAt, copiedPageId);
        _images.Insert(insertAt, images);
        _media.Insert(insertAt, media);
        _textObjects.Insert(insertAt, texts);
        _formulaObjects.Insert(insertAt, formulas);
        _states.Insert(insertAt, state);
        _papers[controller] = paper;
        CurrentIndex = insertAt;
        ApplyCurrentViewState();

        _pageHistory.Push(new DelegateAction("复制页面",
            () =>
            {
                _pages.Insert(insertAt, controller);
                _pageIds.Insert(insertAt, copiedPageId);
                _images.Insert(insertAt, images);
                _media.Insert(insertAt, media);
                _textObjects.Insert(insertAt, texts);
                _formulaObjects.Insert(insertAt, formulas);
                _states.Insert(insertAt, state);
                _papers[controller] = paper;
                CurrentIndex = insertAt;
                ApplyCurrentViewState();
                PageChanged?.Invoke();
                ImagesChanged?.Invoke();
            },
            () =>
            {
                int at = _pages.IndexOf(controller);
                if (at < 0) return;
                _pages.RemoveAt(at);
                _pageIds.RemoveAt(at);
                _images.RemoveAt(at);
                _media.RemoveAt(at);
                _textObjects.RemoveAt(at);
                _formulaObjects.RemoveAt(at);
                _states.RemoveAt(at);
                _papers.Remove(controller);
                if (at < CurrentIndex) CurrentIndex--;
                else if (at == CurrentIndex) CurrentIndex = Math.Min(at, _pages.Count - 1);
                ApplyCurrentViewState();
                PageChanged?.Invoke();
                ImagesChanged?.Invoke();
            }));

        PageChanged?.Invoke();
        ImagesChanged?.Invoke();
        return true;
    }

    /// <summary>Move a page to a new position. <paramref name="toIndex"/> is the final index.</summary>
    public bool MovePage(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= _pages.Count) return false;
        toIndex = Math.Clamp(toIndex, 0, _pages.Count - 1);
        if (fromIndex == toIndex) return false;

        SaveCurrentViewState();
        var currentPage = _pages[CurrentIndex];
        MovePageCore(fromIndex, toIndex);
        CurrentIndex = _pages.IndexOf(currentPage);
        ApplyCurrentViewState();

        _pageHistory.Push(new DelegateAction("移动页面",
            () =>
            {
                MovePageCore(fromIndex, toIndex);
                CurrentIndex = Math.Clamp(CurrentIndex, 0, _pages.Count - 1);
                ApplyCurrentViewState();
                PageChanged?.Invoke();
            },
            () =>
            {
                MovePageCore(toIndex, fromIndex);
                CurrentIndex = Math.Clamp(CurrentIndex, 0, _pages.Count - 1);
                ApplyCurrentViewState();
                PageChanged?.Invoke();
            }));

        PageChanged?.Invoke();
        return true;
    }

    /// <summary>Rename a page. Empty/whitespace names are rejected.</summary>
    public bool RenamePage(int index, string name)
    {
        if (index < 0 || index >= _states.Count) return false;
        string trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return false;
        var oldName = _states[index].Name;
        if (string.Equals(oldName, trimmed, StringComparison.Ordinal)) return false;

        _states[index].Name = trimmed;
        _pageHistory.Push(new DelegateAction("重命名页面",
            () => { _states[index].Name = trimmed; PageChanged?.Invoke(); },
            () => { _states[index].Name = oldName; PageChanged?.Invoke(); }));
        PageChanged?.Invoke();
        return true;
    }

    public string PageName(int index)
        => index >= 0 && index < _states.Count ? _states[index].Name : string.Empty;

    public BoardBackground PageBackground(int index)
        => index >= 0 && index < _states.Count ? _states[index].Background : BoardBackground.Black;

    public float PageGridSpacing(int index)
        => index >= 0 && index < _states.Count ? _states[index].GridSpacing : 0f;

    /// <summary>Metadata for one page, suitable for a text/colour thumbnail in the page list.</summary>
    public BoardPageThumbnail GetPageThumbnail(int index)
    {
        if (index < 0 || index >= _pages.Count) throw new ArgumentOutOfRangeException(nameof(index));
        return new BoardPageThumbnail(
            index, _states[index].Name, _states[index].Background,
            _pages[index].Document.Count, _images[index].Count);
    }

    public IReadOnlyList<BoardPageThumbnail> GetPageThumbnails()
        => Enumerable.Range(0, _pages.Count).Select(GetPageThumbnail).ToList();


    /// <summary>Transfer prepared pages in one UI update. A rejected batch leaves all pages intact.</summary>
    public bool InsertPagesAfterCurrent(IReadOnlyList<(InkController Ink, IReadOnlyList<BoardImageAsset> Images)> pages)
    {
        if (pages is null) return false;
        if (pages.Any(page => page.Ink is null || _pages.Contains(page.Ink) || page.Images is null || page.Images.Any(image => !IsValidImage(image)))
            || pages.Select(page => page.Ink).Distinct().Count() != pages.Count) throw new ArgumentException("Imported pages must have independent controllers and valid images.", nameof(pages));
        if (!CanAddPages(pages.Count)) return false;
        int insertion = CurrentIndex + 1;
        var paper = _papers[Current];
        int insertAt = insertion;
        foreach (var page in pages)
        {
            var controller = page.Ink;
            _pages.Insert(insertAt, controller);
            _pageIds.Insert(insertAt, Guid.NewGuid());
            _images.Insert(insertAt, page.Images.ToList());
            _media.Insert(insertAt, new List<BoardMediaAsset>());
            _textObjects.Insert(insertAt, new List<BoardTextObject>());
            _formulaObjects.Insert(insertAt, new List<BoardFormulaObject>());
            _states.Insert(insertAt, new BoardPageState
            {
                Name = NextPageName(),
                Background = paper.Background,
                GridSpacing = paper.GridSpacing,
                ViewTransform = controller.ViewTransform,
            });
            _papers.Add(controller, paper);
            BindPage(controller);
            insertAt++;
        }
        PageStructureVersion++;
        CurrentIndex = insertion;
        PageChanged?.Invoke(); ImagesChanged?.Invoke();
        return true;
    }

    private static void CarryDrawingState(InkController source, InkController target)
    {
        source.EndPlayback(); target.EndPlayback();
        source.CancelLassoErase();
        source.ClearSelection();
        target.ClearSelection(); target.CloneMode = false;
        target.CurrentAttributes.Color = source.CurrentAttributes.Color;
        target.CurrentAttributes.BaseWidth = source.CurrentAttributes.BaseWidth;
        target.CurrentAttributes.TaperKind = source.CurrentAttributes.TaperKind;
        target.CurrentAttributes.Nib = source.CurrentAttributes.Nib;
        target.CurrentAttributes.IsHighlighter = source.CurrentAttributes.IsHighlighter;
        target.CurrentAttributes.HighlighterAlpha = source.CurrentAttributes.HighlighterAlpha;
        target.EraserMode = source.EraserMode;
        target.EraserRadius = source.EraserRadius;
        target.ShapeKind = source.ShapeKind;
        target.StickyShape = source.StickyShape;
        target.Tool = source.Tool;
    }

    /// <summary>WPF 衍至新页: duplicate every selected object, with one history entry on the new page.</summary>
    public bool CloneSelectionToNewPage()
    {
        if (!CanAddPages() || !Current.HasSelection) return false;
        var strokes = InkStroke.CloneObjects(Current.Selection);
        var ids = Current.SelectedElements.Select(e => e.Id).ToHashSet();
        var images = CurrentImages.Where(image => ids.Contains(image.Id)).Select(image => image.Copy(preserveId: false, copyData: false)).ToList();
        var media = CurrentMedia.Where(item => ids.Contains(item.Id)).Select(item => item.Copy(preserveId: false, copyData: false)).ToList();
        var texts = CurrentTextObjects.Where(text => ids.Contains(text.Id)).Select(text => text.Copy(preserveId: false)).ToList();
        var formulas = CurrentFormulaObjects.Where(formula => ids.Contains(formula.Id)).Select(formula => formula.Copy(preserveId: false)).ToList();
        if (strokes.Count + images.Count + media.Count + texts.Count + formulas.Count == 0) return false;
        if (!InsertPageAfterCurrent()) return false;
        var page = Current;
        var pageImages = _images[CurrentIndex]; var pageMedia = _media[CurrentIndex];
        var pageTexts = _textObjects[CurrentIndex]; var pageFormulas = _formulaObjects[CurrentIndex];
        Apply();
        page.History.Push(new DelegateAction("clone selection to page", Apply, Revert));
        return true;

        void Apply()
        {
            page.Document.AddRange(strokes); pageImages.AddRange(images); pageMedia.AddRange(media);
            foreach (BoardTextObject text in texts)
            {
                text.Attach(this, page);
                if (!pageTexts.Contains(text)) pageTexts.Add(text);
            }
            foreach (BoardFormulaObject formula in formulas)
            {
                formula.Attach(this, page);
                if (!pageFormulas.Contains(formula)) pageFormulas.Add(formula);
            }
            ImagesChanged?.Invoke();
        }
        void Revert()
        {
            page.Document.RemoveAll(strokes); pageImages.RemoveAll(images.Contains); pageMedia.RemoveAll(media.Contains);
            foreach (BoardTextObject text in texts) pageTexts.Remove(text);
            foreach (BoardFormulaObject formula in formulas) pageFormulas.Remove(formula);
            ImagesChanged?.Invoke();
        }
    }

    /// <summary>Clear ink and embedded objects atomically; one undo restores the whole page in order.</summary>
    public void ClearCurrentPage()
    {
        var page = Current;
        page.CancelLassoErase(); page.ClearSelection();
        var pageImages = _images[CurrentIndex]; var pageMedia = _media[CurrentIndex];
        var pageTexts = _textObjects[CurrentIndex]; var pageFormulas = _formulaObjects[CurrentIndex];
        int pageIndex = CurrentIndex;
        var strokes = page.Document.Strokes.ToList(); var images = pageImages.ToList(); var media = pageMedia.ToList();
        var texts = pageTexts.ToList(); var formulas = pageFormulas.ToList();
        BoardImageAsset[] aiImages = images.Where(IsAiBoardImage).ToArray();
        BoardTextObject[] aiTexts = texts.ToArray();
        BoardFormulaObject[] aiFormulas = formulas.ToArray();
        if (strokes.Count + images.Count + media.Count + texts.Count + formulas.Count == 0) return;
        Apply();
        page.History.Push(new DelegateAction("clear board page", Apply, Revert));
        void Apply()
        {
            page.Document.Clear(); pageImages.Clear(); pageMedia.Clear();
            pageTexts.Clear(); pageFormulas.Clear();
            ImagesChanged?.Invoke();
            RaiseAiObjectsChanged("remove", pageIndex, aiImages, null, aiTexts, aiFormulas);
        }
        void Revert()
        {
            page.Document.AddRange(strokes); pageImages.AddRange(images); pageMedia.AddRange(media);
            pageTexts.AddRange(texts); pageFormulas.AddRange(formulas);
            ImagesChanged?.Invoke();
            RaiseAiObjectsChanged("add", pageIndex, aiImages, null, aiTexts, aiFormulas);
        }
    }


    public bool DuplicateCurrentPage() => DuplicateCurrentPage(copyData: false);

    private bool DuplicateCurrentPage(bool copyData)
    {
        if (!CanAddPages()) return false;
        var source = Current;
        var strokes = InkStroke.CloneObjects(source.Document.Strokes);
        var images = CurrentImages.Select(image => image.Copy(preserveId: false, copyData: copyData)).ToList();
        var media = CurrentMedia.Select(item => item.Copy(preserveId: false, copyData: copyData)).ToList();
        var texts = CurrentTextObjects.Select(text => text.Copy(preserveId: false)).ToList();
        var formulas = CurrentFormulaObjects.Select(formula => formula.Copy(preserveId: false)).ToList();
        AddPageAt(CurrentIndex + 1);
        CurrentIndex++;
        _images[CurrentIndex].AddRange(images); _media[CurrentIndex].AddRange(media);
        foreach (BoardTextObject text in texts) { text.Attach(this, Current); _textObjects[CurrentIndex].Add(text); }
        foreach (BoardFormulaObject formula in formulas) { formula.Attach(this, Current); _formulaObjects[CurrentIndex].Add(formula); }
        Current.Document.AddRange(strokes);
        CarryDrawingState(source, Current);
        Current.ViewTransform = source.ViewTransform;
        _states[CurrentIndex].ViewTransform = source.ViewTransform;
        if (CurrentIndex > 0) _states[CurrentIndex].Name = $"{_states[CurrentIndex - 1].Name} 副本";
        PageChanged?.Invoke(); ImagesChanged?.Invoke();
        return true;
    }

    public void RemoveCurrentPage() => RemovePage(CurrentIndex);

    public bool RemovePage(int index)
    {
        if (index < 0 || index >= PageCount) return false;
        if (_pages.Count == 1)
        {
            Current.Clear();
            Current.Selection.Clear();
            Current.History.Clear();
            _images[0].Clear();
            _media[0].Clear();
            _textObjects[0].Clear();
            _formulaObjects[0].Clear();
            _papers[Current] = new BoardPaper(_states[0].Background, _states[0].GridSpacing);
            ImagesChanged?.Invoke();
            PageChanged?.Invoke();
            return true;
        }

        SaveCurrentViewState();

        var controller = _pages[index];
        var images = _images[index];
        var media = _media[index];
        var texts = _textObjects[index];
        var formulas = _formulaObjects[index];
        var state = _states[index];
        var paper = _papers.TryGetValue(controller, out var existingPaper) ? existingPaper : new BoardPaper(state.Background, state.GridSpacing);
        Guid removedPageId = _pageIds[index];
        foreach (var id in images.Select(image => image.Id).Concat(media.Select(item => item.Id))) _selectionElements.Remove(id);

        _pages.RemoveAt(index);
        _pageIds.RemoveAt(index);
        _images.RemoveAt(index);
        _media.RemoveAt(index);
        _textObjects.RemoveAt(index);
        _formulaObjects.RemoveAt(index);
        _states.RemoveAt(index);
        _papers.Remove(controller);
        if (index < CurrentIndex) CurrentIndex--;
        else if (index == CurrentIndex) CurrentIndex = Math.Min(index, _pages.Count - 1);
        ApplyCurrentViewState();
        PageStructureVersion++;

        // Do not dispose the removed controller: undo needs to reattach the exact same instance.
        _pageHistory.Push(new DelegateAction("删除页面",
            () =>
            {
                int at = _pages.IndexOf(controller);
                if (at < 0) return;
                _pages.RemoveAt(at);
                _pageIds.RemoveAt(at);
                _images.RemoveAt(at);
                _media.RemoveAt(at);
                _textObjects.RemoveAt(at);
                _formulaObjects.RemoveAt(at);
                _states.RemoveAt(at);
                _papers.Remove(controller);
                if (at < CurrentIndex) CurrentIndex--;
                else if (at == CurrentIndex) CurrentIndex = Math.Min(at, _pages.Count - 1);
                ApplyCurrentViewState();
                PageStructureVersion++;
                PageChanged?.Invoke();
                ImagesChanged?.Invoke();
            },
            () =>
            {
                _pages.Insert(index, controller);
                _pageIds.Insert(index, removedPageId);
                _images.Insert(index, images);
                _media.Insert(index, media);
                _textObjects.Insert(index, texts);
                _formulaObjects.Insert(index, formulas);
                _states.Insert(index, state);
                _papers[controller] = paper;
                CurrentIndex = index;
                ApplyCurrentViewState();
                PageStructureVersion++;
                PageChanged?.Invoke();
                ImagesChanged?.Invoke();
            }));
        PageChanged?.Invoke();
        ImagesChanged?.Invoke();
        return true;
    }

    public bool AddImage(BoardImageAsset image)
    {
        if (image is null || image.Data is null || image.Data.Length == 0 || image.Width <= 0 || image.Height <= 0)
            return false;

        var pageImages = _images[CurrentIndex];
        int index = pageImages.Count;
        image.LayerOrder = Current.NextLayerOrder();
        pageImages.Add(image);
        Current.History.Push(new DelegateAction("insert image",
            () => { pageImages.Insert(Math.Clamp(index, 0, pageImages.Count), image); ImagesChanged?.Invoke(); },
            () => { pageImages.Remove(image); ImagesChanged?.Invoke(); }));
        ImagesChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// 页令牌校验：不匹配拒绝写入，返回 false。用于 AI 写回时避免把内容写到同索引但已换页的页面。
    /// </summary>
    public bool AddImage(BoardImageAsset image, Guid expectedPageId)
    {
        if (expectedPageId != Guid.Empty && expectedPageId != CurrentPageId)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 拒绝写入图片：页令牌不匹配 expected={expectedPageId} actual={CurrentPageId}", Core.Diagnostics.LogType.Info);
            return false;
        }
        return AddImage(image);
    }

    /// <summary>
    /// 一次插入多张图片并只记录一个撤销步骤（AI 板书整体写入）。
    /// Undo 移除全部、Redo 原顺序插回；返回首次插入的页内索引。
    /// </summary>
    public IReadOnlyList<int> AddImagesAtomic(IReadOnlyList<BoardImageAsset> images, string actionName)
    {
        if (images is null || images.Count == 0) return Array.Empty<int>();

        var inserted = images.Where(IsValidImage).ToArray();
        if (inserted.Length == 0) return Array.Empty<int>();

        var pageImages = _images[CurrentIndex];
        int pageIndex = CurrentIndex;
        int start = pageImages.Count;
        long layerOrder = Current.NextLayerOrder();
        for (int index = 0; index < inserted.Length; index++)
        {
            inserted[index].LayerOrder = layerOrder + index;
            pageImages.Add(inserted[index]);
        }

        string name = string.IsNullOrWhiteSpace(actionName) ? "AI 板书" : actionName.Trim();
        void ApplyInsert()
        {
            int at = Math.Clamp(start, 0, pageImages.Count);
            pageImages.InsertRange(at, inserted);
            ImagesChanged?.Invoke();
            RaiseAiObjectsChanged("add", pageIndex, inserted);
        }

        void RevertInsert()
        {
            foreach (BoardImageAsset image in inserted) pageImages.Remove(image);
            ImagesChanged?.Invoke();
            RaiseAiObjectsChanged("remove", pageIndex, inserted);
        }

        Current.History.Push(new DelegateAction(name, ApplyInsert, RevertInsert));
        ImagesChanged?.Invoke();
        RaiseAiObjectsChanged("add", pageIndex, inserted);
        return Enumerable.Range(start, inserted.Length).ToArray();
    }

    /// <summary>
    /// 页令牌校验：不匹配拒绝写入，返回空。用于 AI 批量写回时避免把内容写到同索引但已换页的页面。
    /// </summary>
    public IReadOnlyList<int> AddImagesAtomic(IReadOnlyList<BoardImageAsset> images, string actionName, Guid expectedPageId)
    {
        if (expectedPageId != Guid.Empty && expectedPageId != CurrentPageId)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 拒绝写入图片批次：页令牌不匹配 expected={expectedPageId} actual={CurrentPageId}", Core.Diagnostics.LogType.Info);
            return Array.Empty<int>();
        }
        return AddImagesAtomic(images, actionName);
    }

    /// <summary>
    /// 一次插入原生文本/公式对象并只记录一个撤销步骤（AI 板书整体写入）。
    /// Undo 移除全部对象，Redo 按原顺序插回；同一次写入共享 <paramref name="groupId"/>，
    /// 返回插入顺序的对象 Id。调用方不传 groupId 时自动生成一个。
    /// </summary>
    public IReadOnlyList<Guid> AddAiObjectsAtomic(
        IReadOnlyList<BoardTextObject> texts,
        IReadOnlyList<BoardFormulaObject> formulas,
        string actionName,
        string groupId = null)
    {
        var insertedTexts = new List<BoardTextObject>();
        foreach (BoardTextObject text in texts ?? Array.Empty<BoardTextObject>())
        {
            if (text is null) continue;
            BoardAiObjectSerialization.Normalize(text);
            if (BoardAiObjectSerialization.IsValid(text)) insertedTexts.Add(text);
        }

        var insertedFormulas = new List<BoardFormulaObject>();
        foreach (BoardFormulaObject formula in formulas ?? Array.Empty<BoardFormulaObject>())
        {
            if (formula is null) continue;
            BoardAiObjectSerialization.Normalize(formula);
            if (BoardAiObjectSerialization.IsValid(formula)) insertedFormulas.Add(formula);
        }

        if (insertedTexts.Count + insertedFormulas.Count == 0) return Array.Empty<Guid>();

        var page = Current;
        int pageIndex = CurrentIndex;
        var pageTexts = _textObjects[pageIndex];
        var pageFormulas = _formulaObjects[pageIndex];
        int textStart = pageTexts.Count;
        int formulaStart = pageFormulas.Count;

        AdaptDefaultAiInkToBackground(_papers[page], insertedTexts, insertedFormulas);

        string group = string.IsNullOrWhiteSpace(groupId) ? Guid.NewGuid().ToString("N") : groupId.Trim();
        long layerOrder = page.NextLayerOrder();
        foreach (BoardTextObject text in insertedTexts)
        {
            text.Attach(this, page);
            text.GroupId = group;
            text.LayerOrder = layerOrder++;
            pageTexts.Add(text);
        }
        foreach (BoardFormulaObject formula in insertedFormulas)
        {
            formula.Attach(this, page);
            formula.GroupId = group;
            formula.LayerOrder = layerOrder++;
            pageFormulas.Add(formula);
        }

        string name = string.IsNullOrWhiteSpace(actionName) ? "AI 板书" : actionName.Trim();

        void ApplyInsert()
        {
            int textAt = Math.Clamp(textStart, 0, pageTexts.Count);
            foreach (BoardTextObject text in insertedTexts)
                if (!pageTexts.Contains(text)) pageTexts.Insert(textAt++, text);
            int formulaAt = Math.Clamp(formulaStart, 0, pageFormulas.Count);
            foreach (BoardFormulaObject formula in insertedFormulas)
                if (!pageFormulas.Contains(formula)) pageFormulas.Insert(formulaAt++, formula);
            ImagesChanged?.Invoke();
            RaiseAiObjectsChanged("add", pageIndex, Array.Empty<BoardImageAsset>(),
                null, insertedTexts, insertedFormulas);
        }

        void RevertInsert()
        {
            foreach (BoardTextObject text in insertedTexts) pageTexts.Remove(text);
            foreach (BoardFormulaObject formula in insertedFormulas) pageFormulas.Remove(formula);
            ImagesChanged?.Invoke();
            RaiseAiObjectsChanged("remove", pageIndex, Array.Empty<BoardImageAsset>(),
                null, insertedTexts, insertedFormulas);
        }

        page.History.Push(new DelegateAction(name, ApplyInsert, RevertInsert));
        ImagesChanged?.Invoke();
        RaiseAiObjectsChanged("add", pageIndex, Array.Empty<BoardImageAsset>(),
            null, insertedTexts, insertedFormulas);
        return insertedTexts.Select(text => text.Id).Concat(insertedFormulas.Select(formula => formula.Id)).ToArray();
    }

    /// <summary>
    /// 页令牌校验：不匹配拒绝写入，返回空。用于 AI 文本/公式写回时避免把内容写到同索引但已换页的页面。
    /// 参数顺序为 texts、formulas、actionName、expectedPageId（必填页令牌）、groupId（可选，末尾默认 null），
    /// 将 expectedPageId 置于必填位以避免与四参数的文本/公式写入重载产生调用二义性；
    /// 页令牌语义：expectedPageId 为 Guid.Empty 时跳过校验，否则必须与当前页 Id 一致才写入，
    /// 不一致时写 [AI] 日志并返回空集合；校验通过则委托给不带页令牌的重载执行实际插入。
    /// </summary>
    public IReadOnlyList<Guid> AddAiObjectsAtomic(
        IReadOnlyList<BoardTextObject> texts,
        IReadOnlyList<BoardFormulaObject> formulas,
        string actionName,
        Guid expectedPageId,
        string groupId = null)
    {
        if (expectedPageId != Guid.Empty && expectedPageId != CurrentPageId)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 拒绝写入板书对象：页令牌不匹配 expected={expectedPageId} actual={CurrentPageId}", Core.Diagnostics.LogType.Info);
            return Array.Empty<Guid>();
        }
        return AddAiObjectsAtomic(texts, formulas, actionName, groupId);
    }

    /// <summary>
    /// 修改一个原生文本对象（正文/标题/样式/几何/分组），整体作为一次撤销步骤。
    /// </summary>
    public bool UpdateAiObject(BoardTextObject target, Action<BoardTextObject> update, string actionName = null)
    {
        if (target is null || update is null) return false;
        var page = Current;
        int pageIndex = CurrentIndex;
        var pageTexts = _textObjects[CurrentIndex];
        BoardTextObject current = pageTexts.FirstOrDefault(text =>
            ReferenceEquals(text, target) || text.Id == target.Id);
        if (current is null) return false;

        var before = current.Copy();
        update(current);
        BoardAiObjectSerialization.Normalize(current);
        if (!BoardAiObjectSerialization.IsValid(current))
        {
            current.CopyStateFrom(before);
            return false;
        }

        var after = current.Copy();
        if (BoardAiObjectSerialization.SameState(before, after)) return false;

        void Apply(BoardTextObject snapshot)
        {
            current.CopyStateFrom(snapshot);
            ImagesChanged?.Invoke();
            RaiseAiObjectsChanged("update", pageIndex, Array.Empty<BoardImageAsset>(),
                null, new[] { current }, null);
        }

        Apply(after);
        page.History.Push(new DelegateAction(
            string.IsNullOrWhiteSpace(actionName) ? "编辑 AI 文本" : actionName.Trim(),
            () => Apply(after), () => Apply(before)));
        ImagesChanged?.Invoke();
        return true;
    }

    /// <summary>修改一个原生公式对象（LaTeX/样式/几何/分组），整体作为一次撤销步骤。</summary>
    public bool UpdateAiObject(BoardFormulaObject target, Action<BoardFormulaObject> update, string actionName = null)
    {
        if (target is null || update is null) return false;
        var page = Current;
        int pageIndex = CurrentIndex;
        var pageFormulas = _formulaObjects[CurrentIndex];
        BoardFormulaObject current = pageFormulas.FirstOrDefault(formula =>
            ReferenceEquals(formula, target) || formula.Id == target.Id);
        if (current is null) return false;

        var before = current.Copy();
        update(current);
        BoardAiObjectSerialization.Normalize(current);
        if (!BoardAiObjectSerialization.IsValid(current))
        {
            current.CopyStateFrom(before);
            return false;
        }

        var after = current.Copy();
        if (BoardAiObjectSerialization.SameState(before, after)) return false;

        void Apply(BoardFormulaObject snapshot)
        {
            current.CopyStateFrom(snapshot);
            ImagesChanged?.Invoke();
            RaiseAiObjectsChanged("update", pageIndex, Array.Empty<BoardImageAsset>(),
                null, null, new[] { current });
        }

        Apply(after);
        page.History.Push(new DelegateAction(
            string.IsNullOrWhiteSpace(actionName) ? "编辑 AI 公式" : actionName.Trim(),
            () => Apply(after), () => Apply(before)));
        ImagesChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// 对当前选中组内的原生文本/公式批量应用连续修改（字号/列/行距/对齐）与过滤删除。
    /// 所有变更合并为一个 <see cref="DelegateAction"/>；不新增对象、不调用 AI。
    /// </summary>
    public bool ApplyAiObjectEdit(
        IEnumerable<ISelectionElement> targets, BoardPlanEditPolicy.BoardObjectEdit edit, string actionName = null)
    {
        if (targets is null || edit is null) return false;
        var ids = targets.Where(element => element is not null).Select(element => element.Id).ToHashSet();
        if (ids.Count == 0) return false;

        var page = Current;
        int pageIndex = CurrentIndex;
        var pageTexts = _textObjects[pageIndex];
        var pageFormulas = _formulaObjects[pageIndex];
        var texts = pageTexts.Where(text => ids.Contains(text.Id)).ToList();
        var formulas = pageFormulas.Where(formula => ids.Contains(formula.Id)).ToList();
        if (texts.Count + formulas.Count == 0) return false;

        var textBefore = texts.ToDictionary(text => text.Id, text => text.Copy());
        var formulaBefore = formulas.ToDictionary(formula => formula.Id, formula => formula.Copy());

        var removedTexts = texts.Where(text => IsRemovedByEdit(ObjectKindOf(text), edit)).ToList();
        var removedFormulas = formulas.Where(_ => IsRemovedByEdit("formula", edit)).ToList();
        var keptTexts = texts.Except(removedTexts).ToList();
        var keptFormulas = formulas.Except(removedFormulas).ToList();

        foreach (BoardTextObject text in keptTexts) ApplyStyleEdit(text, edit);
        foreach (BoardFormulaObject formula in keptFormulas) ApplyStyleEdit(formula, edit);

        foreach (BoardTextObject text in keptTexts)
        {
            BoardAiObjectSerialization.Normalize(text);
            if (!BoardAiObjectSerialization.IsValid(text))
            {
                foreach (BoardTextObject item in keptTexts) item.CopyStateFrom(textBefore[item.Id]);
                foreach (BoardFormulaObject item in keptFormulas) item.CopyStateFrom(formulaBefore[item.Id]);
                return false;
            }
        }
        foreach (BoardFormulaObject formula in keptFormulas)
        {
            BoardAiObjectSerialization.Normalize(formula);
            if (!BoardAiObjectSerialization.IsValid(formula))
            {
                foreach (BoardTextObject item in keptTexts) item.CopyStateFrom(textBefore[item.Id]);
                foreach (BoardFormulaObject item in keptFormulas) item.CopyStateFrom(formulaBefore[item.Id]);
                return false;
            }
        }

        bool styleChanged = keptTexts.Any(text =>
                !BoardAiObjectSerialization.SameState(textBefore[text.Id], text))
            || keptFormulas.Any(formula =>
                !BoardAiObjectSerialization.SameState(formulaBefore[formula.Id], formula));
        if (!styleChanged && removedTexts.Count + removedFormulas.Count == 0) return false;

        var textAfter = keptTexts.ToDictionary(text => text.Id, text => text.Copy());
        var formulaAfter = keptFormulas.ToDictionary(formula => formula.Id, formula => formula.Copy());
        (int Index, BoardTextObject Object)[] removedTextEntries = pageTexts
            .Select((text, index) => (Index: index, Object: text))
            .Where(entry => removedTexts.Contains(entry.Object)).ToArray();
        (int Index, BoardFormulaObject Object)[] removedFormulaEntries = pageFormulas
            .Select((formula, index) => (Index: index, Object: formula))
            .Where(entry => removedFormulas.Contains(entry.Object)).ToArray();

        void ApplyAfter()
        {
            foreach (BoardTextObject text in keptTexts)
                if (textAfter.TryGetValue(text.Id, out BoardTextObject snapshot)) text.CopyStateFrom(snapshot);
            foreach (BoardFormulaObject formula in keptFormulas)
                if (formulaAfter.TryGetValue(formula.Id, out BoardFormulaObject snapshot)) formula.CopyStateFrom(snapshot);
            foreach (var entry in removedTextEntries) pageTexts.Remove(entry.Object);
            foreach (var entry in removedFormulaEntries) pageFormulas.Remove(entry.Object);
            ImagesChanged?.Invoke();
            RaiseAiObjectsChanged("update", pageIndex, Array.Empty<BoardImageAsset>(), null, keptTexts, keptFormulas);
            RaiseAiObjectsChanged("remove", pageIndex, Array.Empty<BoardImageAsset>(), null,
                removedTexts, removedFormulas);
        }

        void ApplyBefore()
        {
            foreach (BoardTextObject text in keptTexts)
                if (textBefore.TryGetValue(text.Id, out BoardTextObject snapshot)) text.CopyStateFrom(snapshot);
            foreach (BoardFormulaObject formula in keptFormulas)
                if (formulaBefore.TryGetValue(formula.Id, out BoardFormulaObject snapshot)) formula.CopyStateFrom(snapshot);
            foreach (var entry in removedTextEntries.OrderBy(entry => entry.Index))
            {
                if (pageTexts.Contains(entry.Object)) continue;
                pageTexts.Insert(Math.Clamp(entry.Index, 0, pageTexts.Count), entry.Object);
                entry.Object.Attach(this, page);
            }
            foreach (var entry in removedFormulaEntries.OrderBy(entry => entry.Index))
            {
                if (pageFormulas.Contains(entry.Object)) continue;
                pageFormulas.Insert(Math.Clamp(entry.Index, 0, pageFormulas.Count), entry.Object);
                entry.Object.Attach(this, page);
            }
            ImagesChanged?.Invoke();
            RaiseAiObjectsChanged("update", pageIndex, Array.Empty<BoardImageAsset>(), null, keptTexts, keptFormulas);
            RaiseAiObjectsChanged("add", pageIndex, Array.Empty<BoardImageAsset>(), null,
                removedTexts, removedFormulas);
        }

        foreach (var entry in removedTextEntries) pageTexts.Remove(entry.Object);
        foreach (var entry in removedFormulaEntries) pageFormulas.Remove(entry.Object);
        ImagesChanged?.Invoke();
        RaiseAiObjectsChanged("update", pageIndex, Array.Empty<BoardImageAsset>(), null, keptTexts, keptFormulas);
        RaiseAiObjectsChanged("remove", pageIndex, Array.Empty<BoardImageAsset>(), null,
            removedTexts, removedFormulas);

        page.History.Push(new DelegateAction(
            string.IsNullOrWhiteSpace(actionName) ? "连续修改 AI 板书" : actionName.Trim(),
            ApplyAfter, ApplyBefore));
        return true;
    }

    private static bool IsRemovedByEdit(string kind, BoardPlanEditPolicy.BoardObjectEdit edit)
    {
        if (edit.KeepKinds.Count > 0 && !edit.KeepKinds.Contains(kind)) return true;
        return edit.RemoveKinds.Contains(kind);
    }

    private static string ObjectKindOf(BoardTextObject text) => (text.Type ?? "").Trim().ToLowerInvariant() switch
    {
        "wordlist" => "wordlist",
        "poem" => "poem",
        _ => "text",
    };

    private static void ApplyStyleEdit(BoardTextObject text, BoardPlanEditPolicy.BoardObjectEdit edit)
    {
        if (edit.ExplicitFontSize is { } explicitFont)
            text.FontSize = ClampAiFont(explicitFont);
        else if (Math.Abs(edit.FontScale - 1) > 0.001)
            text.FontSize = ClampAiFont(text.FontSize * edit.FontScale);

        if (edit.ExplicitLineHeight is { } explicitLine)
            text.LineHeight = Math.Clamp(explicitLine,
                BoardAiObjectSerialization.MinLineHeight, BoardAiObjectSerialization.MaxLineHeight);
        else if (Math.Abs(edit.LineHeightScale - 1) > 0.001)
            text.LineHeight = Math.Clamp(text.LineHeight * edit.LineHeightScale,
                BoardAiObjectSerialization.MinLineHeight, BoardAiObjectSerialization.MaxLineHeight);

        if (edit.Columns is { } columns)
            text.Columns = Math.Clamp(columns, 1, 3);
        if (!string.IsNullOrEmpty(edit.Align))
            text.Align = edit.Align;
    }

    private static void ApplyStyleEdit(BoardFormulaObject formula, BoardPlanEditPolicy.BoardObjectEdit edit)
    {
        if (edit.ExplicitFontSize is { } explicitFont)
            formula.FontSize = ClampAiFont(explicitFont);
        else if (Math.Abs(edit.FontScale - 1) > 0.001)
            formula.FontSize = ClampAiFont(formula.FontSize * edit.FontScale);
    }

    private static double ClampAiFont(double value)
        => Math.Clamp(value, BoardAiObjectSerialization.MinFontSize, BoardAiObjectSerialization.MaxFontSize);

    /// <summary>删除若干原生文本/公式对象，一次撤销按原索引恢复全部。</summary>
    public bool RemoveAiObjects(IEnumerable<ISelectionElement> objects, string actionName = null)
    {
        if (objects is null) return false;
        var page = Current;
        int pageIndex = CurrentIndex;
        var pageTexts = _textObjects[pageIndex];
        var pageFormulas = _formulaObjects[pageIndex];
        var requested = objects.ToArray();
        var textSet = requested.OfType<BoardTextObject>().Where(pageTexts.Contains).ToHashSet();
        var formulaSet = requested.OfType<BoardFormulaObject>().Where(pageFormulas.Contains).ToHashSet();
        if (textSet.Count + formulaSet.Count == 0) return false;

        (int Index, BoardTextObject Object)[] textEntries = pageTexts
            .Select((text, index) => (Index: index, Object: text))
            .Where(entry => textSet.Contains(entry.Object)).ToArray();
        (int Index, BoardFormulaObject Object)[] formulaEntries = pageFormulas
            .Select((formula, index) => (Index: index, Object: formula))
            .Where(entry => formulaSet.Contains(entry.Object)).ToArray();

        void ApplyRemove()
        {
            foreach (var entry in textEntries) pageTexts.Remove(entry.Object);
            foreach (var entry in formulaEntries) pageFormulas.Remove(entry.Object);
            ImagesChanged?.Invoke();
            RaiseAiObjectsChanged("remove", pageIndex, Array.Empty<BoardImageAsset>(), null,
                textEntries.Select(entry => entry.Object).ToArray(),
                formulaEntries.Select(entry => entry.Object).ToArray());
        }

        void ApplyRestore()
        {
            foreach (var entry in textEntries.OrderBy(entry => entry.Index))
            {
                if (pageTexts.Contains(entry.Object)) continue;
                pageTexts.Insert(Math.Clamp(entry.Index, 0, pageTexts.Count), entry.Object);
                entry.Object.Attach(this, page);
            }
            foreach (var entry in formulaEntries.OrderBy(entry => entry.Index))
            {
                if (pageFormulas.Contains(entry.Object)) continue;
                pageFormulas.Insert(Math.Clamp(entry.Index, 0, pageFormulas.Count), entry.Object);
                entry.Object.Attach(this, page);
            }
            ImagesChanged?.Invoke();
            RaiseAiObjectsChanged("add", pageIndex, Array.Empty<BoardImageAsset>(), null,
                textEntries.Select(entry => entry.Object).ToArray(),
                formulaEntries.Select(entry => entry.Object).ToArray());
        }

        ApplyRemove();
        page.History.Push(new DelegateAction(
            string.IsNullOrWhiteSpace(actionName) ? "删除 AI 对象" : actionName.Trim(),
            ApplyRemove, ApplyRestore));
        return true;
    }

    /// <summary>
    /// 清除同一次写入的分组 Id，使组内对象可以独立选择/移动。整体作为一次撤销步骤，
    /// 撤销恢复原 GroupId；返回被取消分组的对象数量。
    /// 阶段 2A 起为 <see cref="UngroupObjects"/> 的薄包装：文本/公式/图片/媒体共用 string GroupId。
    /// </summary>
    public int UngroupAiObjects(string groupId)
        => UngroupObjects(CurrentPageId, groupId, "取消 AI 对象分组");

    /// <summary>
    /// 用图片原子替换原笔画：一次 <see cref="DelegateAction"/> 内删除 <paramref name="strokesToRemove"/>
    /// 并插入 <paramref name="images"/>。传空/null 笔画列表表示删除当前页全部笔画（“替换整页”语义）；
    /// 图片为空或页面没有可删除笔画时返回 <c>false</c>，不会只删不写或只写不删。
    /// Undo 移除新图片并按原索引恢复笔画，Redo 反向，均只占一步。
    /// </summary>
    public bool ReplaceStrokesWithImagesAtomic(
        IReadOnlyList<InkStroke> strokesToRemove,
        IReadOnlyList<BoardImageAsset> images,
        string actionName)
    {
        if (images is null || images.Count == 0) return false;

        var inserted = images.Where(IsValidImage).ToArray();
        if (inserted.Length == 0) return false;

        var page = Current;
        var pageImages = _images[CurrentIndex];
        int pageIndex = CurrentIndex;
        var document = page.Document;

        InkStroke[] removed = strokesToRemove is { Count: > 0 }
            ? strokesToRemove
                .Where(stroke => stroke is not null && document.Strokes.Contains(stroke))
                .Distinct()
                .ToArray()
            : document.Strokes.ToArray();

        if (removed.Length == 0) return false;

        var removedSet = new HashSet<InkStroke>(removed);
        (int Index, InkStroke Stroke)[] indexed = document.Strokes
            .Select((stroke, index) => (Index: index, Stroke: stroke))
            .Where(item => removedSet.Contains(item.Stroke))
            .ToArray();

        int start = pageImages.Count;
        long layerOrder = page.NextLayerOrder();
        for (int index = 0; index < inserted.Length; index++)
            inserted[index].LayerOrder = layerOrder + index;

        void Apply()
        {
            document.RemoveAll(removed);
            int at = Math.Clamp(start, 0, pageImages.Count);
            pageImages.InsertRange(at, inserted);
            ImagesChanged?.Invoke();
            RaiseAiObjectsChanged("replace", pageIndex, inserted, removed);
        }

        void Revert()
        {
            foreach (BoardImageAsset image in inserted) pageImages.Remove(image);
            document.InsertRange(indexed.Select(item => (item.Index, item.Stroke)));
            ImagesChanged?.Invoke();
            RaiseAiObjectsChanged("remove", pageIndex, inserted);
        }

        Apply();
        page.ClearSelection();

        string name = string.IsNullOrWhiteSpace(actionName) ? "AI 板书替换" : actionName.Trim();
        page.History.Push(new DelegateAction(name, Apply, Revert));
        return true;
    }

    /// <summary>
    /// 用原生 AI 对象原子替换原笔画：一次撤销内删除笔画并插入文本/公式对象。
    /// 笔画列表为空表示替换当前页全部笔画；没有可删除笔画或对象无效时返回空（调用方回退为仅插入）。
    /// </summary>
    public IReadOnlyList<Guid> ReplaceStrokesWithAiObjectsAtomic(
        IReadOnlyList<InkStroke> strokesToRemove,
        IReadOnlyList<BoardTextObject> texts,
        IReadOnlyList<BoardFormulaObject> formulas,
        string actionName,
        string groupId = null)
    {
        var insertedTexts = new List<BoardTextObject>();
        foreach (BoardTextObject text in texts ?? Array.Empty<BoardTextObject>())
        {
            if (text is null) continue;
            BoardAiObjectSerialization.Normalize(text);
            if (BoardAiObjectSerialization.IsValid(text)) insertedTexts.Add(text);
        }

        var insertedFormulas = new List<BoardFormulaObject>();
        foreach (BoardFormulaObject formula in formulas ?? Array.Empty<BoardFormulaObject>())
        {
            if (formula is null) continue;
            BoardAiObjectSerialization.Normalize(formula);
            if (BoardAiObjectSerialization.IsValid(formula)) insertedFormulas.Add(formula);
        }

        if (insertedTexts.Count + insertedFormulas.Count == 0) return Array.Empty<Guid>();

        var page = Current;
        int pageIndex = CurrentIndex;
        var document = page.Document;
        var pageTexts = _textObjects[CurrentIndex];
        var pageFormulas = _formulaObjects[CurrentIndex];

        InkStroke[] removed = strokesToRemove is { Count: > 0 }
            ? strokesToRemove
                .Where(stroke => stroke is not null && document.Strokes.Contains(stroke))
                .Distinct()
                .ToArray()
            : document.Strokes.ToArray();
        if (removed.Length == 0) return Array.Empty<Guid>();

        var removedSet = new HashSet<InkStroke>(removed);
        (int Index, InkStroke Stroke)[] indexed = document.Strokes
            .Select((stroke, index) => (Index: index, Stroke: stroke))
            .Where(item => removedSet.Contains(item.Stroke))
            .ToArray();

        int textStart = pageTexts.Count;
        int formulaStart = pageFormulas.Count;
        AdaptDefaultAiInkToBackground(_papers[page], insertedTexts, insertedFormulas);
        string group = string.IsNullOrWhiteSpace(groupId) ? Guid.NewGuid().ToString("N") : groupId.Trim();
        long layerOrder = page.NextLayerOrder();
        foreach (BoardTextObject text in insertedTexts)
        {
            text.Attach(this, page);
            text.GroupId = group;
            text.LayerOrder = layerOrder++;
        }
        foreach (BoardFormulaObject formula in insertedFormulas)
        {
            formula.Attach(this, page);
            formula.GroupId = group;
            formula.LayerOrder = layerOrder++;
        }

        void Apply()
        {
            document.RemoveAll(removed);
            int textAt = Math.Clamp(textStart, 0, pageTexts.Count);
            foreach (BoardTextObject text in insertedTexts)
                if (!pageTexts.Contains(text)) pageTexts.Insert(textAt++, text);
            int formulaAt = Math.Clamp(formulaStart, 0, pageFormulas.Count);
            foreach (BoardFormulaObject formula in insertedFormulas)
                if (!pageFormulas.Contains(formula)) pageFormulas.Insert(formulaAt++, formula);
            ImagesChanged?.Invoke();
            RaiseAiObjectsChanged("replace", pageIndex, Array.Empty<BoardImageAsset>(), removed,
                insertedTexts, insertedFormulas);
        }

        void Revert()
        {
            foreach (BoardTextObject text in insertedTexts) pageTexts.Remove(text);
            foreach (BoardFormulaObject formula in insertedFormulas) pageFormulas.Remove(formula);
            document.InsertRange(indexed.Select(item => (item.Index, item.Stroke)));
            ImagesChanged?.Invoke();
            RaiseAiObjectsChanged("remove", pageIndex, Array.Empty<BoardImageAsset>(), null,
                insertedTexts, insertedFormulas);
        }

        Apply();
        page.ClearSelection();
        page.History.Push(new DelegateAction(
            string.IsNullOrWhiteSpace(actionName) ? "AI 板书替换" : actionName.Trim(),
            Apply, Revert));

        return insertedTexts.Select(text => text.Id)
            .Concat(insertedFormulas.Select(formula => formula.Id))
            .ToArray();
    }

    /// <summary>
    /// 当前页内容包围盒（笔画 + 图片 + 媒体，世界坐标），语义与
    /// <c>SideBoardWindow.CurrentPageContentBounds</c> 一致；图片/媒体按轴对齐矩形计，
    /// 不含旋转后的精确外接框。空页返回 <see cref="Rect.Empty"/>。
    /// </summary>
    public Rect GetContentBounds() => GetContentBounds(CurrentIndex);

    /// <summary>
    /// 指定页内容包围盒（笔画 + 图片 + 媒体，世界坐标）；越界返回空。
    /// 图片/媒体按轴对齐矩形计，供离线讲义/时光机整页渲染使用。
    /// </summary>
    public Rect GetContentBounds(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= _pages.Count) return Rect.Empty;

        Rect bounds = Rect.Empty;
        foreach (InkStroke stroke in _pages[pageIndex].Document.Strokes)
            bounds = bounds.Union(stroke.Bounds);

        foreach (BoardImageAsset image in _images[pageIndex])
        {
            if (image.Data is not { Length: > 0 } || image.Width <= 0 || image.Height <= 0) continue;
            bounds = bounds.Union(new Rect(image.Left, image.Top, image.Left + image.Width, image.Top + image.Height));
        }

        foreach (BoardMediaAsset media in _media[pageIndex])
        {
            if (media.Width <= 0 || media.Height <= 0) continue;
            bounds = bounds.Union(new Rect(media.Left, media.Top, media.Left + media.Width, media.Top + media.Height));
        }

        foreach (BoardTextObject text in _textObjects[pageIndex])
        {
            if (text.Width <= 0 || text.Height <= 0) continue;
            bounds = bounds.Union(text.Bounds);
        }

        foreach (BoardFormulaObject formula in _formulaObjects[pageIndex])
        {
            if (formula.Width <= 0 || formula.Height <= 0) continue;
            bounds = bounds.Union(formula.Bounds);
        }

        return bounds;
    }

    /// <summary>
    /// 同 <see cref="GetContentBounds"/>，但忽略 <paramref name="excludedStrokes"/>（替换原板书前
    /// 用“将被删除的笔画”之外的内容计算自动落点，避免旧笔迹占位导致误判空间不足）。
    /// </summary>
    public Rect GetContentBoundsExcept(IReadOnlyCollection<InkStroke> excludedStrokes)
    {
        HashSet<InkStroke> excluded = excludedStrokes is { Count: > 0 }
            ? new HashSet<InkStroke>(excludedStrokes)
            : null;

        Rect bounds = Rect.Empty;
        foreach (InkStroke stroke in Current.Document.Strokes)
        {
            if (excluded is not null && excluded.Contains(stroke)) continue;
            bounds = bounds.Union(stroke.Bounds);
        }

        foreach (BoardImageAsset image in _images[CurrentIndex])
        {
            if (image.Data is not { Length: > 0 } || image.Width <= 0 || image.Height <= 0) continue;
            bounds = bounds.Union(new Rect(image.Left, image.Top, image.Left + image.Width, image.Top + image.Height));
        }

        foreach (BoardMediaAsset media in _media[CurrentIndex])
        {
            if (media.Width <= 0 || media.Height <= 0) continue;
            bounds = bounds.Union(new Rect(media.Left, media.Top, media.Left + media.Width, media.Top + media.Height));
        }

        foreach (BoardTextObject text in _textObjects[CurrentIndex])
        {
            if (text.Width <= 0 || text.Height <= 0) continue;
            bounds = bounds.Union(text.Bounds);
        }

        foreach (BoardFormulaObject formula in _formulaObjects[CurrentIndex])
        {
            if (formula.Width <= 0 || formula.Height <= 0) continue;
            bounds = bounds.Union(formula.Bounds);
        }

        return bounds;
    }

    public void RemoveImage(BoardImageAsset image)
    {
        if (image is null) return;
        var pageImages = _images[CurrentIndex];
        int index = pageImages.IndexOf(image);
        if (index < 0) return;

        int pageIndex = CurrentIndex;
        bool ai = IsAiBoardImage(image);
        pageImages.RemoveAt(index);
        Current.History.Push(new DelegateAction("delete image",
            () =>
            {
                pageImages.Remove(image);
                ImagesChanged?.Invoke();
                if (ai) RaiseAiObjectsChanged("remove", pageIndex, new[] { image });
            },
            () =>
            {
                pageImages.Insert(Math.Clamp(index, 0, pageImages.Count), image);
                ImagesChanged?.Invoke();
                if (ai) RaiseAiObjectsChanged("add", pageIndex, new[] { image });
            }));
        ImagesChanged?.Invoke();
        if (ai) RaiseAiObjectsChanged("remove", pageIndex, new[] { image });
    }


    /// <summary>
    /// Replace an existing image's raster data and metadata (for example after re-rendering an AI
    /// paste card), keeping its id, position and size. The change is undoable and raises
    /// <see cref="ImagesChanged"/>.
    /// </summary>
    public bool UpdateImage(BoardImageAsset image, byte[] newData, string newMetadata)
    {
        if (image is null || newData is null || newData.Length == 0) return false;
        var pageImages = _images[CurrentIndex];
        var target = pageImages.FirstOrDefault(candidate => candidate.Id == image.Id);
        if (target is null) return false;

        var previousData = target.Data;
        var previousMetadata = target.Metadata ?? "";
        int pageIndex = CurrentIndex;
        bool ai = IsAiBoardImage(target);
        string metadata = newMetadata ?? "";
        target.Data = newData;
        target.Metadata = metadata;

        Current.History.Push(new DelegateAction("update image",
            () =>
            {
                SetImageContent(target, newData, metadata);
                if (ai) RaiseAiObjectsChanged("update", pageIndex, new[] { target });
            },
            () =>
            {
                SetImageContent(target, previousData, previousMetadata);
                if (ai) RaiseAiObjectsChanged("update", pageIndex, new[] { target });
            }));
        ImagesChanged?.Invoke();
        if (ai) RaiseAiObjectsChanged("update", pageIndex, new[] { target });
        return true;
    }


    /// <summary>Record one completed image move/resize as a single page-local undo step.</summary>
    public void RecordImageTransform(BoardImageAsset image, BoardImageGeometry before, BoardImageGeometry after)
    {
        if (image is null || !_images[CurrentIndex].Contains(image) || before == after) return;
        Current.History.Push(new DelegateAction("transform image",
            () => SetImageGeometry(image, after),
            () => SetImageGeometry(image, before)));
        ImagesChanged?.Invoke();
    }

    /// <summary>Insert video bytes as a page-local media object; page undo removes/restores it.</summary>
    public bool AddMedia(BoardMediaAsset media)
    {
        if (!IsValidMedia(media)) return false;
        var pageMedia = _media[CurrentIndex];
        int index = pageMedia.Count;
        media.LayerOrder = Current.NextLayerOrder();
        pageMedia.Add(media);
        Current.History.Push(new DelegateAction("insert media",
            () => { pageMedia.Insert(Math.Clamp(index, 0, pageMedia.Count), media); ImagesChanged?.Invoke(); },
            () => { pageMedia.Remove(media); ImagesChanged?.Invoke(); }));
        ImagesChanged?.Invoke();
        return true;
    }

    public void RemoveMedia(BoardMediaAsset media)
    {
        if (media is null) return;
        var pageMedia = _media[CurrentIndex];
        int index = pageMedia.IndexOf(media);
        if (index < 0) return;
        pageMedia.RemoveAt(index);
        Current.History.Push(new DelegateAction("delete media",
            () => { pageMedia.Remove(media); ImagesChanged?.Invoke(); },
            () => { pageMedia.Insert(Math.Clamp(index, 0, pageMedia.Count), media); ImagesChanged?.Invoke(); }));
        ImagesChanged?.Invoke();
    }

    public void RecordMediaTransform(BoardMediaAsset media, BoardMediaGeometry before, BoardMediaGeometry after)
    {
        if (media is null || !_media[CurrentIndex].Contains(media) || before == after) return;
        Current.History.Push(new DelegateAction("transform media",
            () => SetMediaGeometry(media, after),
            () => SetMediaGeometry(media, before)));
        ImagesChanged?.Invoke();
    }

    /// <summary>
    /// Record one completed element change (move/resize/rotate/flip/gesture) as a single
    /// page-local undo step. <paramref name="before"/> is a <see cref="BoardImageAsset.Copy"/>
    /// snapshot taken by the caller at gesture start; the after-state is captured here.
    /// Element-level manipulation history mirroring the stroke undo path.
    /// </summary>
    public void RecordImageChange(BoardImageAsset image, BoardImageAsset before, string name)
    {
        if (image is null || before is null || !_images[CurrentIndex].Contains(image)) return;
        var after = image.Copy();
        if (SameImageState(before, after)) return;
        Current.History.Push(new DelegateAction(name,
            () => { ApplyImageState(image, after); ImagesChanged?.Invoke(); },
            () => { ApplyImageState(image, before); ImagesChanged?.Invoke(); }));
        ImagesChanged?.Invoke();
    }

    /// <inheritdoc cref="RecordImageChange"/>
    public void RecordMediaChange(BoardMediaAsset media, BoardMediaAsset before, string name)
    {
        if (media is null || before is null || !_media[CurrentIndex].Contains(media)) return;
        var after = media.Copy();
        if (SameMediaState(before, after)) return;
        Current.History.Push(new DelegateAction(name,
            () => { ApplyMediaState(media, after); ImagesChanged?.Invoke(); },
            () => { ApplyMediaState(media, before); ImagesChanged?.Invoke(); }));
        ImagesChanged?.Invoke();
    }

    private static bool SameImageState(BoardImageAsset a, BoardImageAsset b)
        => a.Left == b.Left && a.Top == b.Top && a.Width == b.Width && a.Height == b.Height
           && a.Opacity == b.Opacity
           && BoardElementMath.Same(a.Transform, b.Transform);

    private static bool SameMediaState(BoardMediaAsset a, BoardMediaAsset b)
        => a.Left == b.Left && a.Top == b.Top && a.Width == b.Width && a.Height == b.Height
           && BoardElementMath.Same(a.Transform, b.Transform);

    private static void ApplyImageState(BoardImageAsset image, BoardImageAsset snapshot)
    {
        image.Left = snapshot.Left;
        image.Top = snapshot.Top;
        image.Width = snapshot.Width;
        image.Height = snapshot.Height;
        image.Opacity = snapshot.Opacity;
        image.Transform = snapshot.Transform?.ToArray();
    }

    private static void ApplyMediaState(BoardMediaAsset media, BoardMediaAsset snapshot)
    {
        media.Left = snapshot.Left;
        media.Top = snapshot.Top;
        media.Width = snapshot.Width;
        media.Height = snapshot.Height;
        media.Transform = snapshot.Transform?.ToArray();
    }

    private void ApplyBoard(IReadOnlyList<PreparedPage> loadedPages, int currentIndex = 0)
    {
        foreach (var page in _pages) page.Dispose();
        _pages.Clear();
        _pageIds.Clear();
        _papers.Clear();
        _pageHistory.Clear();
        _nextPageNumber = 1;
        _images.Clear();
        _media.Clear();
        _textObjects.Clear();
        _formulaObjects.Clear();
        _states.Clear();
        _selectionElements.Clear();
        foreach (var page in loadedPages)
        {
            var controller = new InkController();
            var viewTransform = FromTransformArray(page.ViewTransform);
            BindPage(controller);
            controller.Document.AddRange(page.Strokes);
            controller.ViewTransform = viewTransform;
            _pages.Add(controller);
            _pageIds.Add(page.PageId != Guid.Empty ? page.PageId : Guid.NewGuid());
            _papers.Add(controller, page.Paper with { Name = page.Paper.Name ?? $"页面 {_nextPageNumber}" });
            _nextPageNumber++;
            controller.ViewTransform = page.View;
            if (page.Viewport is { Length: 2 } && page.Viewport.All(value => float.IsFinite(value) && value > 0))
                controller.ViewCenter = new(page.Viewport[0] / 2, page.Viewport[1] / 2);
            _images.Add(page.Images);
            _media.Add(page.Media);
            var texts = page.TextObjects ?? new List<BoardTextObject>();
            var formulas = page.FormulaObjects ?? new List<BoardFormulaObject>();
            foreach (BoardTextObject text in texts) text.Attach(this, controller);
            foreach (BoardFormulaObject formula in formulas) formula.Attach(this, controller);
            _textObjects.Add(texts);
            _formulaObjects.Add(formulas);
            _states.Add(new BoardPageState
            {
                Name = string.IsNullOrWhiteSpace(page.Name) ? $"页面 {_states.Count + 1}" : page.Name,
                Background = page.Paper.Background,
                GridSpacing = page.Paper.GridSpacing,
                ViewTransform = viewTransform,
            });
        }
        CurrentIndex = loadedPages.Count == 0 ? 0 : Math.Clamp(currentIndex, 0, loadedPages.Count - 1);
        PageStructureVersion++;
        PageChanged?.Invoke();
        ImagesChanged?.Invoke();
    }

private sealed record PreparedPage(List<InkStroke> Strokes, List<BoardImageAsset> Images, List<BoardMediaAsset> Media, BoardPaper Paper,
        string Name = null, float[] ViewTransform = null,
        List<BoardTextObject> TextObjects = null, List<BoardFormulaObject> FormulaObjects = null)
    {
        public Matrix3x2 View { get; init; } = Matrix3x2.Identity;

        public float[] Viewport { get; init; }

        /// <summary>页身份，用于写回时校验同索引换页 ≠ 同页；缺失时进程内生成。</summary>
        public Guid PageId { get; init; } = Guid.Empty;

        /// <summary>页内对象版本号；旧文件缺失时为 0，由对象增删改接线方递增。</summary>
        public int ObjectsVersion { get; init; }
    }

    private static PreparedPage PreparePage(BoardPageFile page, BoardPaper fallback)
    {
        var strokes = (page.Strokes ?? new()).Select(FromStrokeFile).ToList();
        ShapeSource.ShareDefinitions(strokes);
        var texts = (page.TextObjects ?? new()).Select(BoardAiObjectSerialization.FromFile)
            .Where(BoardAiObjectSerialization.IsValid).ToList();
        var formulas = (page.FormulaObjects ?? new()).Select(BoardAiObjectSerialization.FromFile)
            .Where(BoardAiObjectSerialization.IsValid).ToList();
        long nextLayer = strokes.Count == 0 ? 0 : strokes.Max(stroke => stroke.LayerOrder ?? 0) + 1;
        foreach (BoardTextObject text in texts)
            if (text.LayerOrder is null) text.LayerOrder = nextLayer++;
        foreach (BoardFormulaObject formula in formulas)
            if (formula.LayerOrder is null) formula.LayerOrder = nextLayer++;
        var paper = ReadPaper(page.Background, page.GridSpacing, fallback);
        AdaptDefaultAiInkToBackground(paper, texts, formulas);
        return new(strokes, (page.Images ?? new()).Where(IsValidImage).ToList(),
            (page.Media ?? new()).Where(IsValidMedia).ToList(), paper,
            string.IsNullOrWhiteSpace(page.Name) ? null : page.Name.Trim(), page.ViewTransform, texts, formulas)
        { View = ReadViewTransform(page.ViewTransform), Viewport = page.Viewport,
            PageId = page.PageId is Guid id && id != Guid.Empty ? id : Guid.NewGuid(),
            ObjectsVersion = Math.Max(0, page.ObjectsVersion) };
    }

    private static BoardPaper ReadPaper(string background, float? spacing, BoardPaper fallback) => new(
        Enum.TryParse(background, true, out BoardBackground parsed) && Enum.IsDefined(parsed) ? parsed : fallback.Background,
        spacing is float value && float.IsFinite(value) && value > 0 ? Math.Clamp(value, 12f, 160f) : fallback.GridSpacing);

    private static Matrix3x2 ReadViewTransform(float[] values)
    {
        if (values is not { Length: 6 } || !values.All(float.IsFinite)) return Matrix3x2.Identity;
        var transform = new Matrix3x2(values[0], values[1], values[2], values[3], values[4], values[5]);
        return Matrix3x2.Invert(transform, out _) ? transform : Matrix3x2.Identity;
    }

    private void ReplacePage(int index, PreparedPage page)
    {
        var controller = _pages[index];
        if (page.PageId != Guid.Empty && index < _pageIds.Count) _pageIds[index] = page.PageId;
        controller.CancelLassoErase(); controller.ClearSelection();
        controller.Document.Clear();
        controller.History.Clear();

        controller.Selection.Clear();
        var viewTransform = FromTransformArray(page.ViewTransform);
        controller.ViewTransform = viewTransform;
        controller.Document.AddRange(page.Strokes);
        _images[index] = page.Images;
        _media[index] = page.Media;
        var texts = page.TextObjects ?? new List<BoardTextObject>();
        var formulas = page.FormulaObjects ?? new List<BoardFormulaObject>();
        foreach (BoardTextObject text in texts) text.Attach(this, controller);
        foreach (BoardFormulaObject formula in formulas) formula.Attach(this, controller);
        _textObjects[index] = texts;
        _formulaObjects[index] = formulas;
        _papers[controller] = page.Paper;

        if (!string.IsNullOrWhiteSpace(page.Name)) _states[index].Name = page.Name;
        _states[index].Background = page.Paper.Background;
        _states[index].GridSpacing = page.Paper.GridSpacing;
        _states[index].ViewTransform = viewTransform;
        _pageHistory.Clear();
    }

    private void AddPageAt(int index)
    {

        var basis = _states[Math.Clamp(CurrentIndex, 0, _states.Count - 1)];
        var page = new InkController();
        BindPage(page);
        _pages.Insert(index, page);
        _pageIds.Insert(index, Guid.NewGuid());
        _images.Insert(index, new List<BoardImageAsset>());
        _media.Insert(index, new List<BoardMediaAsset>());
        _textObjects.Insert(index, new List<BoardTextObject>());
        _formulaObjects.Insert(index, new List<BoardFormulaObject>());
        _states.Insert(index, new BoardPageState
        {
            Name = NextPageName(),
            Background = basis.Background,
            GridSpacing = basis.GridSpacing,
            ViewTransform = Matrix3x2.Identity,
        });
        _papers.Add(page, new BoardPaper(basis.Background, basis.GridSpacing));
        PageStructureVersion++;
    }

    private void MovePageCore(int fromIndex, int toIndex)
    {
        var page = _pages[fromIndex];
        var pageId = _pageIds[fromIndex];
        _pages.RemoveAt(fromIndex);
        _pages.Insert(toIndex, page);
        _pageIds.RemoveAt(fromIndex);
        _pageIds.Insert(toIndex, pageId);

        var images = _images[fromIndex];
        _images.RemoveAt(fromIndex);
        _images.Insert(toIndex, images);

        var media = _media[fromIndex];
        _media.RemoveAt(fromIndex);
        _media.Insert(toIndex, media);

        var texts = _textObjects[fromIndex];
        _textObjects.RemoveAt(fromIndex);
        _textObjects.Insert(toIndex, texts);

        var formulas = _formulaObjects[fromIndex];
        _formulaObjects.RemoveAt(fromIndex);
        _formulaObjects.Insert(toIndex, formulas);

        var state = _states[fromIndex];
        _states.RemoveAt(fromIndex);
        _states.Insert(toIndex, state);
        PageStructureVersion++;
    }

    private string NextPageName()
    {
        int number = _states.Count + 1;
        string name = $"页面 {number}";
        while (_states.Any(state => string.Equals(state.Name, name, StringComparison.Ordinal)))
        {
            number++;
            name = $"页面 {number}";
        }
        return name;
    }

    private void SyncPaper(int index)
    {
        if (index < 0 || index >= _pages.Count || index >= _states.Count) return;
        _papers[_pages[index]] = new BoardPaper(_states[index].Background, _states[index].GridSpacing);
    }

    private void SaveCurrentViewState()
    {
        if (CurrentIndex >= 0 && CurrentIndex < _states.Count && CurrentIndex < _pages.Count)
            _states[CurrentIndex].ViewTransform = _pages[CurrentIndex].ViewTransform;
    }

    private void ApplyCurrentViewState()
    {
        if (CurrentIndex >= 0 && CurrentIndex < _states.Count && CurrentIndex < _pages.Count)
            _pages[CurrentIndex].ViewTransform = _states[CurrentIndex].ViewTransform;
    }

    private static float[] ToTransformArray(Matrix3x2 transform)
        => new[] { transform.M11, transform.M12, transform.M21, transform.M22, transform.M31, transform.M32 };

    private static Matrix3x2 FromTransformArray(float[] values)
    {
        if (values is null || values.Length < 6) return Matrix3x2.Identity;
        return new Matrix3x2(values[0], values[1], values[2], values[3], values[4], values[5]);

    }

    private static bool IsValidImage(BoardImageAsset image)
        => image is not null && image.Data is { Length: > 0 } && image.Width > 0 && image.Height > 0;

private static bool IsValidMedia(BoardMediaAsset media)
        => media is not null && media.Data is { Length: > 0 } && media.Width > 0 && media.Height > 0
           && media.ContentType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true;

    private void SetImageContent(BoardImageAsset image, byte[] data, string metadata)
    {
        image.Data = data;
        image.Metadata = metadata ?? "";
        ImagesChanged?.Invoke();
    }
/// <summary>AI 卡片判定：metadata 必须是 <see cref="AiPasteMetadata"/> 风格且包含 kind 字段。</summary>
    private static bool IsAiBoardImage(BoardImageAsset image)
    {
        if (image is null || string.IsNullOrWhiteSpace(image.Metadata)) return false;
        if (image.Metadata.IndexOf("\"kind\"", StringComparison.OrdinalIgnoreCase) < 0) return false;
        return AiPasteMetadata.TryParse(image.Metadata) is not null;
    }

    private const string DefaultDarkAiInk = "#FF202024";
    private const string DefaultLightAiInk = "#FFF5F5F8";

    /// <summary>
    /// AI 写入使用统一默认墨色；在黑板/绿板等深色页面上把默认深色字切换为浅色，
    /// 避免“对象存在、选择框可见但字不可见”。只替换 AI 的默认色，不覆盖用户自选颜色。
    /// </summary>
    private static void AdaptDefaultAiInkToBackground(BoardPaper paper,
        IReadOnlyList<BoardTextObject> texts, IReadOnlyList<BoardFormulaObject> formulas)
    {
        bool darkBackground = paper.Background is BoardBackground.Black or BoardBackground.Green;
        string source = darkBackground ? DefaultDarkAiInk : DefaultLightAiInk;
        string target = darkBackground ? DefaultLightAiInk : DefaultDarkAiInk;
        int changed = 0;
        foreach (BoardTextObject text in texts)
        {
            if (!string.Equals(text.Source, "ai", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsDefaultInk(text.Color, source)) { text.Color = target; changed++; }
        }
        foreach (BoardFormulaObject formula in formulas)
        {
            if (!string.Equals(formula.Source, "ai", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsDefaultInk(formula.Color, source)) { formula.Color = target; changed++; }
        }
        if (changed > 0)
            InkCanvas.Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 板书对象墨色适配背景：background={paper.Background}，changed={changed}，"
                + $"target={target}", InkCanvas.Core.Diagnostics.LogType.Trace);
    }

    private static bool IsDefaultInk(string color, string expected)
    {
        string actual = (color ?? "").Trim().TrimStart('#');
        return string.Equals(actual, expected.TrimStart('#'), StringComparison.OrdinalIgnoreCase);
    }

    private void RaiseAiObjectsChanged(string action, int pageIndex,
        IReadOnlyList<BoardImageAsset> objects,
        IReadOnlyList<InkStroke> removedStrokes = null,
        IReadOnlyList<BoardTextObject> texts = null,
        IReadOnlyList<BoardFormulaObject> formulas = null)
    {
        var imageSnapshot = objects?.Where(item => item is not null).ToArray() ?? Array.Empty<BoardImageAsset>();
        var textSnapshot = texts?.Where(item => item is not null).ToArray() ?? Array.Empty<BoardTextObject>();
        var formulaSnapshot = formulas?.Where(item => item is not null).ToArray() ?? Array.Empty<BoardFormulaObject>();
        if (imageSnapshot.Length + textSnapshot.Length + formulaSnapshot.Length == 0) return;

        AiObjectsChanged?.Invoke(new BoardAiObjectChange
        {
            Action = action,
            PageIndex = pageIndex,
            Objects = imageSnapshot,
            Texts = textSnapshot,
            Formulas = formulaSnapshot,
            RemovedStrokes = removedStrokes ?? Array.Empty<InkStroke>(),
        });
    }


    private void SetImageGeometry(BoardImageAsset image, BoardImageGeometry geometry)
    {
        image.Left = geometry.Left;
        image.Top = geometry.Top;
        image.Width = geometry.Width;
        image.Height = geometry.Height;
        ImagesChanged?.Invoke();
    }

    private void SetMediaGeometry(BoardMediaAsset media, BoardMediaGeometry geometry)
    {
        media.Left = geometry.Left;
        media.Top = geometry.Top;
        media.Width = geometry.Width;
        media.Height = geometry.Height;
        ImagesChanged?.Invoke();
    }

    private static StrokeFile ToStrokeFile(InkStroke stroke) => new()
    {
        Id = stroke.Id,
        InkWash = stroke.Attributes.InkWash,
        InkWashSeed = stroke.Attributes.InkWashSeed,
        LayerOrder = stroke.LayerOrder,
        Color = stroke.Attributes.Color.PackedArgb,
        Width = stroke.Attributes.BaseWidth,
        Highlighter = stroke.Attributes.IsHighlighter,
        Taper = (int)stroke.Attributes.TaperKind,
        Nib = stroke.Attributes.Nib,
        NibRotation = stroke.Attributes.NibRotation,
        Shape = stroke.IsRecognizedShape,
        ShapeSource = stroke.ShapeSource,
        HighlighterAlpha = stroke.Attributes.HighlighterAlpha,
        ObjectFrame = stroke.ExportObjectFrame(),
        Points = stroke.Points.Select(point => point.HasPressure || point.TiltX != 0 || point.TiltY != 0
            ? new[] { point.X, point.Y, point.Pressure, point.Width, point.TimeMs, point.TiltX, point.TiltY, point.HasPressure ? 1f : 0f }
            : new[] { point.X, point.Y, point.Pressure, point.Width, point.TimeMs }).ToList(),
    };

    private static InkStroke FromStrokeFile(StrokeFile strokeFile)
    {
        var attributes = new StrokeAttributes
        {
            Color = InkColor.FromPackedArgb(strokeFile.Color),
            BaseWidth = strokeFile.Width,
            Nib = Enum.IsDefined(strokeFile.Nib) ? strokeFile.Nib : PenNib.Round,
            NibRotation = float.IsFinite(strokeFile.NibRotation) ? strokeFile.NibRotation : 0,
            IsHighlighter = strokeFile.Highlighter,
            HighlighterAlpha = strokeFile.HighlighterAlpha,
            TaperKind = Enum.IsDefined(typeof(TaperKind), strokeFile.Taper)
                ? (TaperKind)strokeFile.Taper : TaperKind.None,
            InkWash = strokeFile.InkWash.Normalize(),
            InkWashSeed = strokeFile.InkWashSeed,
        };
        var stroke = new InkStroke(attributes) { Id = strokeFile.Id ?? Guid.NewGuid(), IsRecognizedShape = strokeFile.Shape, LayerOrder = strokeFile.LayerOrder, ShapeSource = strokeFile.ShapeSource };
        stroke.RestorePoints((strokeFile.Points ?? new List<float[]>())
            .Where(row => row.Length >= 4)
            .Select(row => new InkPoint(row[0], row[1], row[2], row[3], row.Length > 4 ? row[4] : 0f,
                row.Length > 7 && row[7] > 0, row.Length > 5 ? row[5] : 0, row.Length > 6 ? row[6] : 0)));
        stroke.Complete();
        stroke.ImportObjectFrame(strokeFile.ObjectFrame);
        return stroke;
    }

    private sealed class BoardFile
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public Dictionary<string, byte[]> Extensions { get; set; } = new(StringComparer.Ordinal);
        [System.Text.Json.Serialization.JsonIgnore]
        public BoardArchiveMetadata Archive { get; set; }
        public string Format { get; set; }
        public int Version { get; set; }
        public string Background { get; set; }
        public float GridSpacing { get; set; }
        public List<BoardPageFile> Pages { get; set; } = new();
        public BoardSideFile SideBoard { get; set; }
        public List<BoardBlobReference> Blobs { get; set; } = new();
        public List<StrokeFile> Strokes { get; set; }
    }

    private sealed class BoardPageFile
    {
        public float[] Viewport { get; set; }
        public string Name { get; set; }
        public float[] ViewTransform { get; set; }
        public string Background { get; set; }
        public float? GridSpacing { get; set; }
        public List<StrokeFile> Strokes { get; set; } = new();
        public List<BoardImageAsset> Images { get; set; } = new();
        public List<BoardMediaAsset> Media { get; set; } = new();

        /// <summary>Optional since v4: absent in older files, treated as empty.</summary>
        public List<BoardTextObjectFile> TextObjects { get; set; } = new();

        /// <summary>Optional since v4: absent in older files, treated as empty.</summary>
        public List<BoardFormulaObjectFile> FormulaObjects { get; set; } = new();

        /// <summary>v4 可选字段：页身份，用于写回时校验同索引换页 ≠ 同页；缺失时加载阶段生成。</summary>
        public Guid? PageId { get; set; }

        /// <summary>v4 可选字段：页内对象版本号，由对象增删改接线方递增；旧文件缺失时为 0。</summary>
        public int ObjectsVersion { get; set; }
    }

    private sealed class BoardSideFile
    {
        public int Version { get; set; }
        public int PageIndex { get; set; }
        public List<BoardPageFile> Pages { get; set; } = new();
    }

    private sealed class StrokeFile
    {
        public Guid? Id { get; set; }
        public InkWashBrushOptions InkWash { get; set; } = InkWashBrushOptions.Default;
        public int InkWashSeed { get; set; }
        public long? LayerOrder { get; set; }
        public uint Color { get; set; }
        public float Width { get; set; }
        public bool Highlighter { get; set; }
        public int Taper { get; set; }
        public PenNib Nib { get; set; }
        public float NibRotation { get; set; }
        public bool Shape { get; set; }
        public ShapeSource ShapeSource { get; set; }
        public byte HighlighterAlpha { get; set; } = 96;
        public float[] ObjectFrame { get; set; }
        public List<float[]> Points { get; set; } = new();
    }
}
