namespace InkCanvas.Controls.Controls;

/// <summary>
/// Icon codepoints, named so call sites never contain raw private-use characters.
/// Values are <b>Fluent System Icons</b> (MIT, microsoft/fluentui-system-icons, 24-filled)
/// codepoints mapped 1:1 from the original Segoe MDL2/Fluent IDs — see parity/icon-map.json
/// (the Segoe codepoint stays the cross-platform stable ID; both platforms render the same
/// MIT font, identical across Windows 10/11). <see cref="Shapes"/> is a geometric character
/// from Segoe UI Symbol (set <c>ShapesFont</c> when using it); <see cref="Ai"/> is a sentinel.
/// </summary>
public static class Glyphs
{
    public const string Pen = "\uF3DD";
    public const string Highlight = "\uF481";
    public const string Eraser = "\uE5F2";
    public const string Select = "\uF6A1";
    public const string Spotlight = "\uF7A4";

    public const string Shapes = "△";
    public const string ShapesFont = "Segoe UI Symbol";

    /// <summary>Stable assistant command ID. Hosts render <see cref="Assistant"/> through AiGlyph.</summary>
    public const string Ai = "AI";
    public const string Assistant = "\uEB3D";
    public const string Agent = "\uF1F9";
    public const string Hint = "\uF4E1";

    public const string Undo = "\uF19A";
    public const string Redo = "\uF16F";
    public const string Delete = "\uF34D";
    public const string Add = "\uF10A";
    public const string AddPage = "\uE4E8";
    public const string ExitBoard = "\uF6E3";

    public const string Settings = "\uF6B3";
    public const string Camera = "\uF255";
    public const string Video = "\uF865";
    public const string Stopwatch = "\uF840";
    public const string People = "\uF5B3";
    public const string Board = "\uF8C3";

    public const string ChevronUp = "\uF2B7";
    public const string ChevronDown = "\uF2A4";
    public const string ChevronLeft = "\uF2AB";
    public const string ChevronRight = "\uF2B1";

    public const string Lock = "\uE79E";
    public const string Unlock = "\uE7A4";
    public const string Close = "\uF36A";
    public const string Exit = "\uF36A";

    public const string Play = "\uF610";
    public const string Pause = "\uF5AC";
    public const string Restart = "\uF1A0";
    public const string Replay = "\uF140";
    public const string Help = "\uF648";
    public const string Info = "\uF4AB";
    public const string Download = "\uF151";
    public const string SlideList = "\uF6F3";

    public const string Pdf = "\uF3AD";
    public const string Import = "\uF15A";
    public const string Calculator = "\uE247";
    public const string Pages = "\uE52D";
    public const string Search = "\uF69A";
    public const string SaveCopy = "\uF68D";
    public const string ZoomIn = "\uF8DD";
    public const string ZoomOut = "\uF8DF";
    public const string FitPage = "\uF160";
    public const string FitWidth = "\uF8E6";
    public const string Hand = "\uE6ED";
    public const string TimeMachine = "\uF486";
    // ---- Exact WPF floating-bar codepoints (upstream MainWindow.xaml) ----
    public const string Cursor = "\uE450";
    public const string PenWpf = "\uF253";
    public const string HighlightWpf = "\uF481";
    public const string EraserOverlayCircle = "\uF2BC";
    public const string EraserOverlayStroke = "\uEBCB";
    public const string SelectWpf = "\uF6A1";
    public const string SelectOverlay = "\uF2C0";
    public const string ShapesWpf = "\uF6B7";
    public const string InsertMedia = "\uF490";
    public const string BoardWpf = "\uF8C3";
    public const string Tools = "\uF467";
    public const string FoldWpf = "\uF166";
    public const string Emoji = "\uF3E0";
    public const string EmojiDrag = "\uF3C1";
    public const string Gesture = "\uE6E5";
    public const string TimerWpf = "\uF2E2";
    public const string Rand = "\uEF37";
    public const string SaveStrokes = "\uF68A";
    public const string OpenStrokes = "\uF433";
    public const string ReplayWpf = "\uF140";
    public const string GuideWpf = "\uE173";
    public const string ExitWpf = "\uF619";
    public const string HideControl = "\uE603";
    public const string LRSwitch = "\uF18E";
    public const string GestureTranslate = "\uF169";
    public const string GestureZoom = "\uF8DD";
    public const string GestureRotate = "\uF13E";
}
