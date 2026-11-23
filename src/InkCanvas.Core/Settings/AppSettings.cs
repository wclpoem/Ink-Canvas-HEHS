using System.Text.Json;
using System.Text.Json.Serialization;

namespace InkCanvas.Core.Settings;

/// <summary>
/// Application settings. JSON property names are kept identical to the WPF original so an
/// existing <c>Settings.json</c> loads unchanged. Serialised with System.Text.Json instead of
/// Newtonsoft (one less dependency); the three former public fields are now properties with
/// the same JSON names, which is wire-compatible.
/// </summary>
public sealed class AppSettings
{
    [JsonPropertyName("advanced")] public AdvancedSettings Advanced { get; set; } = new();
    [JsonPropertyName("appearance")] public AppearanceSettings Appearance { get; set; } = new();
    [JsonPropertyName("automation")] public AutomationSettings Automation { get; set; } = new();
    [JsonPropertyName("behavior")] public PowerPointSettings PowerPoint { get; set; } = new();
    [JsonPropertyName("canvas")] public CanvasSettings Canvas { get; set; } = new();
    [JsonPropertyName("gesture")] public GestureSettings Gesture { get; set; } = new();
    [JsonPropertyName("inkToShape")] public InkToShapeSettings InkToShape { get; set; } = new();
    [JsonPropertyName("startup")] public StartupSettings Startup { get; set; } = new();
    [JsonPropertyName("randSettings")] public RandSettings Rand { get; set; } = new();

    /// <summary>旧版实验特性开关；保留节点以保证旧 Settings.json 读入后不丢字段。</summary>
    [JsonPropertyName("experimental")] public ExperimentalSettings Experimental { get; set; } = new();
    [JsonPropertyName("display")] public DisplaySettings Display { get; set; } = new();
    [JsonPropertyName("storage")] public StorageSettings Storage { get; set; } = new();
    [JsonPropertyName("archive")] public ArchiveSettings Archive { get; set; } = new();

    private HotkeySettings _hotkey = new();

    /// <summary>可配置热键；旧设置文件缺少该节点（或显式 null）时回退为默认值。</summary>
    [JsonPropertyName("hotkey")]
    public HotkeySettings Hotkey
    {
        get => _hotkey ??= new HotkeySettings();
        set => _hotkey = value ?? new HotkeySettings();
    }

    private SideBoardSettings _sideBoard = new();

    /// <summary>板中板窗口位置/尺寸；缺省或四个值不全时使用默认右侧抽屉。</summary>
    [JsonPropertyName("sideBoard")]
    public SideBoardSettings SideBoard
    {
        get => _sideBoard ??= new SideBoardSettings();
        set => _sideBoard = value ?? new SideBoardSettings();
    }


    private AiSettings _ai = new();

    /// <summary>AI 助教功能开关；旧设置文件缺少该节点或显式写成 null 时回退为默认（全开）。</summary>
    [JsonPropertyName("ai")]
    public AiSettings Ai
    {
        get => _ai ??= new AiSettings();
        set => _ai = value ?? new AiSettings();
    }

    private ClassroomSettings _classroom = new();

    /// <summary>课堂场景与工具配置；旧设置文件缺少节点时回退为默认。</summary>
    [JsonPropertyName("classroom")]
    public ClassroomSettings Classroom
    {
        get => _classroom ??= new ClassroomSettings();
        set => _classroom = value ?? new ClassroomSettings();
    }

    /// <summary>新功能回退开关；旧设置文件缺该节点时保持默认（全部开启）。</summary>
    [JsonPropertyName("features")] public FeatureSettings Features { get; set; } = new();

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this)) ?? new AppSettings();

    /// <summary>
    /// 1:1 port of upstream SetSettingsToRecommendation() (MW_Settings.cs): the "重置设置"
    /// default set. Mutates in place instead of replacing the instance so live references
    /// held by services (AutoSaveService's Automation, Palm's Advanced) stay valid; the
    /// caller re-applies and saves afterwards. Upstream keeps the auto-delete configuration
    /// across a reset, and fields it does not list fall back to the constructor defaults
    /// (which match upstream Settings.cs).
    /// </summary>
    public void ResetToRecommendation()
    {
        bool autoDelSavedFiles = Automation.AutoDelSavedFiles;
        int autoDelDaysThreshold = Automation.AutoDelSavedFilesDaysThreshold;

        Advanced.IsSpecialScreen = true;
        Advanced.IsQuadIR = false;
        Advanced.TouchMultiplier = 0.3;
        Advanced.NibModeBoundsWidth = 5;
        Advanced.FingerModeBoundsWidth = 20;
        Advanced.NibModeBoundsWidthThresholdValue = 2.5;
        Advanced.FingerModeBoundsWidthThresholdValue = 2.5;
        Advanced.NibModeBoundsWidthEraserSize = 0.8;
        Advanced.FingerModeBoundsWidthEraserSize = 0.8;
        Advanced.IsLogEnabled = true;
        Advanced.IsSecondConfimeWhenShutdownApp = false;
        Advanced.IsEnableEdgeGestureUtil = false;

        Appearance.IsEnableDisPlayFloatBarText = false;
        Appearance.IsEnableDisPlayNibModeToggler = false;
        Appearance.IsColorfulViewboxFloatingBar = false;
        Appearance.FloatingBarScale = 100.0;
        Appearance.BlackboardScale = 100.0;
        Appearance.FloatingBarBottomMargin = 100.0; // upstream constructor default
        Appearance.IsTransparentButtonBackground = true;
        Appearance.IsShowExitButton = true;
        Appearance.IsShowEraserButton = true;
        Appearance.IsShowHideControlButton = false;
        Appearance.IsShowLRSwitchButton = false;
        Appearance.IsShowModeFingerToggleSwitch = true;
        Appearance.Theme = 0;

        Automation.IsAutoFoldInEasiNote = true;
        Automation.IsAutoFoldInEasiNoteIgnoreDesktopAnno = true;
        Automation.IsAutoFoldInEasiCamera = true;
        Automation.IsAutoFoldInEasiNote3C = false;
        Automation.IsAutoFoldInSeewoPincoTeacher = false;
        Automation.IsAutoFoldInHiteTouchPro = false;
        Automation.IsAutoFoldInHiteCamera = false;
        Automation.IsAutoFoldInWxBoardMain = false;
        Automation.IsAutoFoldInOldZyBoard = false;
        Automation.IsAutoFoldInMSWhiteboard = false;
        Automation.IsAutoFoldInPPTSlideShow = false;
        Automation.IsAutoKillPptService = false;
        Automation.IsAutoKillEasiNote = false;
        Automation.IsSaveScreenshotsInDateFolders = false;
        Automation.IsAutoSaveStrokesAtScreenshot = true;
        Automation.IsAutoSaveStrokesAtClear = true;
        Automation.IsAutoClearWhenExitingWritingMode = false;
        Automation.MinimumAutomationStrokeNumber = 0;
        Automation.AutoSavedStrokesLocation = @"D:\Ink Canvas"; // upstream constructor default
        Automation.AutoDelSavedFiles = autoDelSavedFiles;
        Automation.AutoDelSavedFilesDaysThreshold = autoDelDaysThreshold;

        PowerPoint.IsShowPPTNavigationBottom = false;
        PowerPoint.IsShowPPTNavigationSides = true; // upstream constructor default
        PowerPoint.IsShowBottomPPTNavigationPanel = true;
        PowerPoint.IsShowSidePPTNavigationPanel = true;
        PowerPoint.PowerPointSupport = true;
        PowerPoint.IsShowCanvasAtNewSlideShow = true;
        PowerPoint.IsNoClearStrokeOnSelectWhenInPowerPoint = true;
        PowerPoint.IsShowStrokeOnSelectInPowerPoint = false;
        PowerPoint.HideStrokesAfterLeavingAnnotation = false;
        PowerPoint.IsAutoSaveStrokesInPowerPoint = true;
        PowerPoint.IsAutoSaveScreenShotInPowerPoint = true;
        PowerPoint.IsNotifyPreviousPage = false;
        PowerPoint.IsNotifyHiddenPage = false;
        PowerPoint.IsNotifyAutoPlayPresentation = true;
        PowerPoint.IsEnableTwoFingerGestureInPresentationMode = false;
        PowerPoint.IsEnableFingerGestureSlideShowControl = false;
        PowerPoint.IsSupportWPS = true;

        Canvas.InkWidth = 2.5;
        Canvas.HighlighterWidth = null;
        Canvas.InkAlpha = 80;
        Canvas.IsCompressPicturesUploaded = false; // upstream constructor default
        Canvas.IsShowCursor = false;
        Canvas.InkStyle = 0;
        Canvas.PenNib = Ink.PenNib.Round;
        Canvas.InkWashWidth = 30;
        Canvas.InkWash = Ink.InkWashBrushOptions.Default;
        Canvas.PenColor = null;
        Canvas.HighlighterColor = null;
        Canvas.ShapeKind = null;
        Canvas.EraserSize = 1;
        Canvas.EraserType = 0;
        Canvas.HideStrokeWhenSelecting = false;
        Canvas.UsingWhiteboard = false;

        Gesture.MatrixTransformCenterPoint = MatrixTransformCenterPointOptions.CanvasCenterPoint;
        Gesture.AutoSwitchTwoFingerGesture = true;
        Gesture.IsEnableTwoFingerTranslate = true;
        Gesture.IsEnableTwoFingerZoom = false;
        Gesture.IsEnableTwoFingerRotation = false;
        Gesture.IsEnableTwoFingerRotationOnSelection = true;
        Gesture.IsEnableMultiTouchMode = true; // upstream constructor default

        InkToShape.IsInkToShapeEnabled = true;
        InkToShape = new();
        Canvas.UseHardwarePressure = true;
        Canvas.UsePenTilt = true;
        Gesture.PenHasPriority = true;
        Gesture.PalmErases = true;

        Startup.IsEnableNibMode = false;
        Startup.IsAutoUpdate = true;
        Startup.IsAutoUpdateWithSilence = true;
        Startup.AutoUpdateWithSilenceStartTime = "18:20";
        Startup.AutoUpdateWithSilenceEndTime = "07:40";
        Startup.IsFoldAtStartup = false;
        Display = new();

        Rand.PeopleCount = 60; // upstream constructor default
        Rand.IsNotRepeatName = false;
        Rand.SamplingMode = LotterySamplingMode.SoftCooldown;
        Rand.CooldownHalfLifeDays = 10;
        Rand.CooldownPenaltyStrength = 1;
        Experimental = new ExperimentalSettings();
    }
}

public sealed class StorageSettings
{
    public bool AutomaticallyCleanCache { get; set; } = true;
    public int UpdateCacheDays { get; set; } = 7;
}

/// <summary>
/// 旧版实验特性开关。迁移后未参与实际逻辑，但原样读写，避免旧 Settings.json 字段丢失。
/// </summary>
public sealed class ExperimentalSettings
{
    [JsonPropertyName("ScreenContentTracking")] public bool ScreenContentTracking { get; set; }
    [JsonPropertyName("FollowScroll")] public bool FollowScroll { get; set; } = true;
    [JsonPropertyName("FollowZoom")] public bool FollowZoom { get; set; } = true;
    [JsonPropertyName("MaskFixedRegions")] public bool MaskFixedRegions { get; set; } = true;
    [JsonPropertyName("RememberPages")] public bool RememberPages { get; set; } = true;
    [JsonPropertyName("PreserveUnchangedRegions")] public bool PreserveUnchangedRegions { get; set; } = true;
    [JsonPropertyName("Word")] public bool Word { get; set; } = true;
    [JsonPropertyName("PowerPoint")] public bool PowerPoint { get; set; } = true;
    [JsonPropertyName("Edge")] public bool Edge { get; set; } = true;
}

/// <summary>第一轮功能区开关，供后续新功能按需回退；默认全部开启。</summary>
public sealed class FeatureSettings
{
    [JsonPropertyName("enableAiTeaching")] public bool EnableAiTeaching { get; set; } = true;
    [JsonPropertyName("enableExerciseFlow")] public bool EnableExerciseFlow { get; set; } = true;
    [JsonPropertyName("enableExampleLibrary")] public bool EnableExampleLibrary { get; set; } = true;
    [JsonPropertyName("enableResourceBundle")] public bool EnableResourceBundle { get; set; } = true;
}

/// <summary>
/// AI 助教灰度开关：每一项关闭时回退到旧的单段、无圈选、无自检或纯 PNG 贴回行为。
/// 默认全部开启，便于旧版 Settings.json 直接升级。
/// </summary>
public sealed class AiSettings
{
    public AiDocumentSettings Documents { get; set; } = new();
    [JsonPropertyName("enableLocalCorrection")] public bool EnableLocalCorrection { get; set; } = true;
    [JsonPropertyName("enableEditablePaste")] public bool EnableEditablePaste { get; set; } = true;
    [JsonPropertyName("enableVerification")] public bool EnableVerification { get; set; } = true;
    [JsonPropertyName("enableTwoStage")] public bool EnableTwoStage { get; set; } = true;

    /// <summary>上次使用的讲题模式，缺省为分步引导（课堂默认不泄露答案）。</summary>
    [JsonPropertyName("teachingMode")] public string TeachingMode { get; set; } = "step";

    /// <summary>
    /// 是否已展示过「框选后可用 AI 识别」的一次性引导。旧设置文件缺该键时为 false，
    /// 因此升级后的首次使用仍会提示一次；之后不再反复打扰。
    /// </summary>
    [JsonPropertyName("hasSeenLassoHint")] public bool HasSeenLassoHint { get; set; }
}

public sealed class AiDocumentSettings
{
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = "";
    public string Model { get; set; } = "";
    public bool AssistantProtocol { get; set; }
}

public enum OptionalOperation { Yes, No, Ask }

public sealed class CanvasSettings
{
    public double InkWashWidth { get; set; } = 30;
    public Ink.InkWashBrushOptions InkWash { get; set; } = Ink.InkWashBrushOptions.Default;
    public bool UseHardwarePressure { get; set; } = true;
    public bool UsePenTilt { get; set; } = true;
    [JsonPropertyName("inkWidth")] public double InkWidth { get; set; } = 2.5;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("highlighterWidth")] public double? HighlighterWidth { get; set; }
    [JsonPropertyName("inkAlpha")] public int InkAlpha { get; set; } = 140;
    [JsonPropertyName("isCompressPicturesUploaded")] public bool IsCompressPicturesUploaded { get; set; }
    [JsonPropertyName("isShowCursor")] public bool IsShowCursor { get; set; }

    /// <summary>Persisted pen-style index, mapped by <see cref="OriginalMenuCatalog.ToTaperKind"/>.</summary>
    [JsonPropertyName("inkStyle")] public int InkStyle { get; set; }
    [JsonPropertyName("penNib")] public Ink.PenNib PenNib { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("penColor")] public uint? PenColor { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("highlighterColor")] public uint? HighlighterColor { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("shapeKind")] public Shapes.ShapeKind? ShapeKind { get; set; }


    [JsonPropertyName("eraserSize")] public int EraserSize { get; set; } = 2;

    /// <summary>0 = legacy default, 1 = area eraser, 2 = stroke eraser, 3 = lasso eraser.</summary>
    [JsonPropertyName("eraserType")] public int EraserType { get; set; }

    [JsonPropertyName("hideStrokeWhenSelecting")] public bool HideStrokeWhenSelecting { get; set; } = true;
    [JsonPropertyName("usingWhiteboard")] public bool UsingWhiteboard { get; set; }
    [JsonPropertyName("hyperbolaAsymptoteOption")] public OptionalOperation HyperbolaAsymptoteOption { get; set; } = OptionalOperation.Ask;
}

public enum MatrixTransformCenterPointOptions
{
    CanvasCenterPoint,
    SelectedElementsCenterPoint,
    GestureOperationCenterPoint,
}

public sealed class GestureSettings
{
    public bool PenHasPriority { get; set; } = true;
    public bool PalmErases { get; set; } = true;
    [JsonPropertyName("isEnableMultiTouchMode")] public bool IsEnableMultiTouchMode { get; set; } = true;
    [JsonPropertyName("isEnableTwoFingerZoom")] public bool IsEnableTwoFingerZoom { get; set; } = true;
    [JsonPropertyName("isEnableTwoFingerTranslate")] public bool IsEnableTwoFingerTranslate { get; set; } = true;
    [JsonPropertyName("AutoSwitchTwoFingerGesture")] public bool AutoSwitchTwoFingerGesture { get; set; } = true;
    [JsonPropertyName("isEnableTwoFingerRotation")] public bool IsEnableTwoFingerRotation { get; set; }
    [JsonPropertyName("isEnableTwoFingerRotationOnSelection")] public bool IsEnableTwoFingerRotationOnSelection { get; set; }
    /// <summary>HarmonyOS port: stylus double-tap/squeeze tool cycling (PR #4). Unused on Windows but persisted for cross-platform Settings.json parity.</summary>
    [JsonPropertyName("isEnableStylusGesture")] public bool IsEnableStylusGesture { get; set; } = true;
    [JsonPropertyName("matrixTransformCenterPoint")] public MatrixTransformCenterPointOptions MatrixTransformCenterPoint { get; set; } = MatrixTransformCenterPointOptions.CanvasCenterPoint;


    [JsonIgnore]
    public bool IsEnableTwoFingerGesture => IsEnableTwoFingerZoom || IsEnableTwoFingerTranslate || IsEnableTwoFingerRotation;

    [JsonIgnore]
    public bool IsEnableTwoFingerGestureTranslateOrRotation => IsEnableTwoFingerTranslate || IsEnableTwoFingerRotation;
}

public sealed class StartupSettings
{
    [JsonPropertyName("isAutoUpdate")] public bool IsAutoUpdate { get; set; } = true;
    [JsonPropertyName("isAutoUpdateWithSilence")] public bool IsAutoUpdateWithSilence { get; set; }
    [JsonPropertyName("isAutoUpdateWithSilenceStartTime")] public string AutoUpdateWithSilenceStartTime { get; set; } = "00:00";
    [JsonPropertyName("isAutoUpdateWithSilenceEndTime")] public string AutoUpdateWithSilenceEndTime { get; set; } = "00:00";
    [JsonPropertyName("isEnableNibMode")] public bool IsEnableNibMode { get; set; }
    [JsonPropertyName("isFoldAtStartup")] public bool IsFoldAtStartup { get; set; }
}

public sealed class DisplaySettings
{
    [JsonPropertyName("allDisplays")] public bool AllDisplays { get; set; } = true;
    [JsonPropertyName("enabledDevices")] public List<string> EnabledDevices { get; set; } = new();
}

/// <summary>快捷键配置。字符串形式便于手工编辑 Settings.json；无效值回退默认。</summary>
public sealed class HotkeySettings
{
    private HotkeyBindingSettings _sideBoard = new();

    [JsonPropertyName("sideBoard")]
    public HotkeyBindingSettings SideBoard
    {
        get => _sideBoard ??= new HotkeyBindingSettings();
        set => _sideBoard = value ?? new HotkeyBindingSettings();
    }
}

/// <summary>单个快捷键：修饰键（Alt/Control/Shift/Win，可用 + 组合）与主键（字母、数字或 F1-F24）。</summary>
public sealed class HotkeyBindingSettings
{
    [JsonPropertyName("modifiers")] public string Modifiers { get; set; } = "Alt";
    [JsonPropertyName("key")] public string Key { get; set; } = "N";
}

/// <summary>板中板窗口记忆矩形（物理像素）。任一字段为空即视为未保存。</summary>
public sealed class SideBoardSettings
{
    [JsonPropertyName("x")] public int? X { get; set; }
    [JsonPropertyName("y")] public int? Y { get; set; }
    [JsonPropertyName("w")] public int? W { get; set; }
    [JsonPropertyName("h")] public int? H { get; set; }

    [JsonIgnore]
    public bool HasSavedBounds => X.HasValue && Y.HasValue && W is > 0 && H is > 0;
}

public sealed class AppearanceSettings
{
    [JsonPropertyName("isEnableDisPlayFloatBarText")] public bool IsEnableDisPlayFloatBarText { get; set; }
    [JsonPropertyName("isEnableDisPlayNibModeToggler")] public bool IsEnableDisPlayNibModeToggler { get; set; } = true;
    [JsonPropertyName("isColorfulViewboxFloatingBar")] public bool IsColorfulViewboxFloatingBar { get; set; }
    [JsonPropertyName("enableViewboxFloatingBarScaleTransform")] public double FloatingBarScale { get; set; } = 100.0;
    [JsonPropertyName("enableViewboxBlackBoardScaleTransform")] public double BlackboardScale { get; set; } = 100.0;
    [JsonPropertyName("floatingBarBottomMargin")] public double FloatingBarBottomMargin { get; set; } = 100.0;
    [JsonPropertyName("isTransparentButtonBackground")] public bool IsTransparentButtonBackground { get; set; } = true;
    /// <summary>Icon style: Fluent System Icons Regular (default) vs Filled.</summary>
    [JsonPropertyName("isIconRegularStyle")] public bool IsIconRegularStyle { get; set; } = true;
    [JsonPropertyName("isShowExitButton")] public bool IsShowExitButton { get; set; } = true;
    [JsonPropertyName("isShowEraserButton")] public bool IsShowEraserButton { get; set; } = true;
    [JsonPropertyName("isShowHideControlButton")] public bool IsShowHideControlButton { get; set; }
    [JsonPropertyName("isShowLRSwitchButton")] public bool IsShowLRSwitchButton { get; set; }
    [JsonPropertyName("isShowModeFingerToggleSwitch")] public bool IsShowModeFingerToggleSwitch { get; set; } = true;

    /// <summary>0 = follow system, 1 = light, 2 = dark.</summary>
    [JsonPropertyName("theme")] public int Theme { get; set; }
}

public sealed class PowerPointSettings
{
    [JsonPropertyName("isShowPPTNavigationBottom")] public bool IsShowPPTNavigationBottom { get; set; } = true;
    [JsonPropertyName("isShowPPTNavigationSides")] public bool IsShowPPTNavigationSides { get; set; } = true;
    [JsonPropertyName("isShowBottomPPTNavigationPanel")] public bool IsShowBottomPPTNavigationPanel { get; set; } = true;
    [JsonPropertyName("isShowSidePPTNavigationPanel")] public bool IsShowSidePPTNavigationPanel { get; set; } = true;
    [JsonPropertyName("powerPointSupport")] public bool PowerPointSupport { get; set; } = true;
    [JsonPropertyName("isShowCanvasAtNewSlideShow")] public bool IsShowCanvasAtNewSlideShow { get; set; } = true;
    [JsonPropertyName("isNoClearStrokeOnSelectWhenInPowerPoint")] public bool IsNoClearStrokeOnSelectWhenInPowerPoint { get; set; } = true;
    [JsonPropertyName("isShowStrokeOnSelectInPowerPoint")] public bool IsShowStrokeOnSelectInPowerPoint { get; set; }
    [JsonPropertyName("hideStrokesAfterLeavingAnnotation")] public bool HideStrokesAfterLeavingAnnotation { get; set; }
    [JsonPropertyName("isAutoSaveStrokesInPowerPoint")] public bool IsAutoSaveStrokesInPowerPoint { get; set; } = true;
    [JsonPropertyName("isAutoSaveScreenShotInPowerPoint")] public bool IsAutoSaveScreenShotInPowerPoint { get; set; }
    [JsonPropertyName("isNotifyPreviousPage")] public bool IsNotifyPreviousPage { get; set; }
    [JsonPropertyName("isNotifyHiddenPage")] public bool IsNotifyHiddenPage { get; set; } = true;
    [JsonPropertyName("isNotifyAutoPlayPresentation")] public bool IsNotifyAutoPlayPresentation { get; set; } = true;
    [JsonPropertyName("isEnableTwoFingerGestureInPresentationMode")] public bool IsEnableTwoFingerGestureInPresentationMode { get; set; }
    [JsonPropertyName("isEnableFingerGestureSlideShowControl")] public bool IsEnableFingerGestureSlideShowControl { get; set; } = true;
    [JsonPropertyName("isSupportWPS")] public bool IsSupportWPS { get; set; } = true;
}

public sealed class AutomationSettings
{
    [JsonPropertyName("isAutoFoldInEasiNote")] public bool IsAutoFoldInEasiNote { get; set; }
    [JsonPropertyName("isAutoFoldInEasiNoteIgnoreDesktopAnno")] public bool IsAutoFoldInEasiNoteIgnoreDesktopAnno { get; set; }
    [JsonPropertyName("isAutoFoldInEasiCamera")] public bool IsAutoFoldInEasiCamera { get; set; }
    [JsonPropertyName("isAutoFoldInEasiNote3C")] public bool IsAutoFoldInEasiNote3C { get; set; }
    [JsonPropertyName("isAutoFoldInSeewoPincoTeacher")] public bool IsAutoFoldInSeewoPincoTeacher { get; set; }
    [JsonPropertyName("isAutoFoldInHiteTouchPro")] public bool IsAutoFoldInHiteTouchPro { get; set; }
    [JsonPropertyName("isAutoFoldInHiteCamera")] public bool IsAutoFoldInHiteCamera { get; set; }
    [JsonPropertyName("isAutoFoldInWxBoardMain")] public bool IsAutoFoldInWxBoardMain { get; set; }
    [JsonPropertyName("isAutoFoldInOldZyBoard")] public bool IsAutoFoldInOldZyBoard { get; set; }
    [JsonPropertyName("isAutoFoldInMSWhiteboard")] public bool IsAutoFoldInMSWhiteboard { get; set; }
    [JsonPropertyName("isAutoFoldInPPTSlideShow")] public bool IsAutoFoldInPPTSlideShow { get; set; }
    [JsonPropertyName("isAutoKillPptService")] public bool IsAutoKillPptService { get; set; }
    [JsonPropertyName("isAutoKillEasiNote")] public bool IsAutoKillEasiNote { get; set; }
    [JsonPropertyName("isSaveScreenshotsInDateFolders")] public bool IsSaveScreenshotsInDateFolders { get; set; }
    [JsonPropertyName("isAutoSaveStrokesAtScreenshot")] public bool IsAutoSaveStrokesAtScreenshot { get; set; }
    [JsonPropertyName("isAutoSaveStrokesAtClear")] public bool IsAutoSaveStrokesAtClear { get; set; }
    [JsonPropertyName("isAutoClearWhenExitingWritingMode")] public bool IsAutoClearWhenExitingWritingMode { get; set; }
    [JsonPropertyName("minimumAutomationStrokeNumber")] public int MinimumAutomationStrokeNumber { get; set; }
    [JsonPropertyName("autoSavedStrokesLocation")] public string AutoSavedStrokesLocation { get; set; } = @"D:\Ink Canvas";
    [JsonPropertyName("autoDelSavedFiles")] public bool AutoDelSavedFiles { get; set; }
    [JsonPropertyName("autoDelSavedFilesDaysThreshold")] public int AutoDelSavedFilesDaysThreshold { get; set; } = 15;

    [JsonIgnore]
    public bool IsEnableAutoFold =>
        IsAutoFoldInEasiNote || IsAutoFoldInEasiCamera || IsAutoFoldInEasiNote3C ||
        IsAutoFoldInSeewoPincoTeacher || IsAutoFoldInHiteTouchPro || IsAutoFoldInHiteCamera ||
        IsAutoFoldInWxBoardMain || IsAutoFoldInOldZyBoard || IsAutoFoldInPPTSlideShow ||
        IsAutoFoldInMSWhiteboard;
}

public sealed class AdvancedSettings
{
    [JsonPropertyName("isSpecialScreen")] public bool IsSpecialScreen { get; set; }
    [JsonPropertyName("isQuadIR")] public bool IsQuadIR { get; set; }
    [JsonPropertyName("touchMultiplier")] public double TouchMultiplier { get; set; } = 0.25;
    [JsonPropertyName("nibModeBoundsWidth")] public int NibModeBoundsWidth { get; set; } = 10;
    [JsonPropertyName("fingerModeBoundsWidth")] public int FingerModeBoundsWidth { get; set; } = 30;
    [JsonPropertyName("nibModeBoundsWidthThresholdValue")] public double NibModeBoundsWidthThresholdValue { get; set; } = 2.5;
    [JsonPropertyName("fingerModeBoundsWidthThresholdValue")] public double FingerModeBoundsWidthThresholdValue { get; set; } = 2.5;
    [JsonPropertyName("nibModeBoundsWidthEraserSize")] public double NibModeBoundsWidthEraserSize { get; set; } = 0.8;
    [JsonPropertyName("fingerModeBoundsWidthEraserSize")] public double FingerModeBoundsWidthEraserSize { get; set; } = 0.8;
    [JsonPropertyName("isEnableEdgeGestureUtil")] public bool IsEnableEdgeGestureUtil { get; set; }
    [JsonPropertyName("isLogEnabled")] public bool IsLogEnabled { get; set; } = true;
    [JsonPropertyName("isSecondConfimeWhenShutdownApp")] public bool IsSecondConfimeWhenShutdownApp { get; set; }
}

public sealed class InkToShapeSettings
{
    [JsonPropertyName("isInkToShapeEnabled")] public bool IsInkToShapeEnabled { get; set; } = true;
    public int HoldMilliseconds { get; set; } = 450;
    public bool AutoAlign { get; set; } = true;
    public double AlignmentAngle { get; set; } = 5;
    public bool RecognizePolygons { get; set; } = true;
    public bool RecognizeRoundShapes { get; set; } = true;
    public bool RecognizeCube { get; set; } = true;
    public bool RecognizeAxes { get; set; } = true;
}

public sealed class RandSettings
{
    private LotterySamplingMode _samplingMode;
    private double _halfLifeDays = 10, _penaltyStrength = 1;
    [JsonPropertyName("peopleCount")] public int PeopleCount { get; set; } = 60;
    [JsonPropertyName("isNotRepeatName")]
    public bool IsNotRepeatName
    {
        get => SamplingMode == LotterySamplingMode.RoundWithoutReplacement;
        set
        {
            if (value) _samplingMode = LotterySamplingMode.RoundWithoutReplacement;
            else if (_samplingMode == LotterySamplingMode.RoundWithoutReplacement) _samplingMode = LotterySamplingMode.SoftCooldown;
        }
    }
    [JsonPropertyName("samplingMode")]
    public LotterySamplingMode SamplingMode
    {
        get => _samplingMode;
        set => _samplingMode = Enum.IsDefined(value) ? value : LotterySamplingMode.SoftCooldown;
    }
    [JsonPropertyName("cooldownHalfLifeDays")]
    public double CooldownHalfLifeDays
    {
        get => _halfLifeDays;
        set => _halfLifeDays = double.IsFinite(value) ? Math.Clamp(value, .25, 3650) : 10;
    }
    [JsonPropertyName("cooldownPenaltyStrength")]
    public double CooldownPenaltyStrength
    {
        get => _penaltyStrength;
        set => _penaltyStrength = double.IsFinite(value) ? Math.Clamp(value, .1, 10) : 1;
    }
}


/// <summary>课堂工具箱配置：场景预设、倒计时与常用参数；新字段只追加。</summary>
public sealed class ClassroomSettings
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;

    [JsonPropertyName("presetId")] public string PresetId { get; set; } = "teach";

    [JsonPropertyName("countdownMinutes")] public int CountdownMinutes { get; set; } = 3;

    [JsonPropertyName("toolkitCollapsed")] public bool ToolkitCollapsed { get; set; }

    [JsonPropertyName("practicePageName")] public string PracticePageName { get; set; } = "随堂练习";

    [JsonPropertyName("customPresets")] public string CustomPresets { get; set; } = "";

    [JsonPropertyName("groupSplitMode")] public string GroupSplitMode { get; set; } = "groupCount";

    [JsonPropertyName("groupSplitValue")] public int GroupSplitValue { get; set; } = 4;

    [JsonPropertyName("spotlightRegions")] public string SpotlightRegions { get; set; } = "";

    /// <summary>工具箱窗口屏幕位置；int.MinValue 表示从未保存，按默认居中放置。</summary>
    [JsonPropertyName("toolkitX")] public int ToolkitX { get; set; } = int.MinValue;
    [JsonPropertyName("toolkitY")] public int ToolkitY { get; set; } = int.MinValue;

    /// <summary>放大镜窗口屏幕位置；int.MinValue 表示未保存。</summary>
    [JsonPropertyName("magnifierX")] public int MagnifierX { get; set; } = int.MinValue;
    [JsonPropertyName("magnifierY")] public int MagnifierY { get; set; } = int.MinValue;
}

public enum LotterySamplingMode { SoftCooldown, Uniform, RoundWithoutReplacement }
