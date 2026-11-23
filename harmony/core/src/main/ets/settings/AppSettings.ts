// @parity src/InkCanvas.Core/Settings/AppSettings.cs
// 字段名采用 Settings.json 的 JSON 键名（camelCase/历史大小写原样），便于编解码与互读。
// 枚举（theme/inkStyle/penNib/eraserSize/eraserType/matrixTransformCenterPoint）以数字存储。

export interface AdvancedSettings {
  isSpecialScreen: boolean;
  isQuadIR: boolean;
  touchMultiplier: number;
  nibModeBoundsWidth: number;
  fingerModeBoundsWidth: number;
  nibModeBoundsWidthThresholdValue: number;
  fingerModeBoundsWidthThresholdValue: number;
  nibModeBoundsWidthEraserSize: number;
  fingerModeBoundsWidthEraserSize: number;
  isEnableEdgeGestureUtil: boolean;
  isLogEnabled: boolean;
  isSecondConfimeWhenShutdownApp: boolean;
}

export interface AppearanceSettings {
  isEnableDisPlayFloatBarText: boolean;
  isEnableDisPlayNibModeToggler: boolean;
  isColorfulViewboxFloatingBar: boolean;
  enableViewboxFloatingBarScaleTransform: number;
  enableViewboxBlackBoardScaleTransform: number;
  floatingBarBottomMargin: number;
  isTransparentButtonBackground: boolean;
  // 图标风格：Fluent System Icons Regular（true）/ Filled（false，默认）。
  isIconRegularStyle: boolean;
  isShowExitButton: boolean;
  isShowEraserButton: boolean;
  isShowHideControlButton: boolean;
  isShowLRSwitchButton: boolean;
  isShowModeFingerToggleSwitch: boolean;
  theme: number;
}

export interface CanvasSettings {
  // 基线演进（PascalCase 键）：墨韵宽度/墨韵参数（嵌套对象）/硬件压感/笔倾斜。
  InkWashWidth: number;
  InkWash: InkWashOptions;
  UseHardwarePressure: boolean;
  UsePenTilt: boolean;
  // 笔/荧光笔颜色与图形种类（可空；null 省略写出，C# JsonIgnore WhenWritingNull）。
  penColor?: number | null;
  highlighterColor?: number | null;
  shapeKind?: number | null;
  inkWidth: number;
  // 荧光笔独立宽度（null/缺省 = 跟随笔宽）；写出时 null 省略（C# JsonIgnore WhenWritingNull）。
  highlighterWidth?: number | null;
  inkAlpha: number;
  isCompressPicturesUploaded: boolean;
  isShowCursor: boolean;
  inkStyle: number;
  eraserSize: number;
  eraserType: number;
  hideStrokeWhenSelecting: boolean;
  usingWhiteboard: boolean;
  penNib: number;
}

export interface GestureSettings {
  // 基线演进（PascalCase 键）：笔优先/手掌擦除（PalmRejector 顶层参数）。
  PenHasPriority: boolean;
  PalmErases: boolean;
  isEnableMultiTouchMode: boolean;
  isEnableTwoFingerZoom: boolean;
  isEnableTwoFingerTranslate: boolean;
  AutoSwitchTwoFingerGesture: boolean;
  isEnableTwoFingerRotation: boolean;
  isEnableTwoFingerRotationOnSelection: boolean;
  matrixTransformCenterPoint: number;
  // 手写笔手势（双击/轻捏）切换工具开关；默认开启。
  isEnableStylusGesture: boolean;
}

export interface PowerPointSettings {
  isShowPPTNavigationBottom: boolean;
  isShowPPTNavigationSides: boolean;
  isShowBottomPPTNavigationPanel: boolean;
  isShowSidePPTNavigationPanel: boolean;
  powerPointSupport: boolean;
  isShowCanvasAtNewSlideShow: boolean;
  isNoClearStrokeOnSelectWhenInPowerPoint: boolean;
  isShowStrokeOnSelectInPowerPoint: boolean;
  // 离开批注模式后隐藏墨迹（基线演进新键）。
  hideStrokesAfterLeavingAnnotation: boolean;
  isAutoSaveStrokesInPowerPoint: boolean;
  isAutoSaveScreenShotInPowerPoint: boolean;
  isNotifyPreviousPage: boolean;
  isNotifyHiddenPage: boolean;
  isNotifyAutoPlayPresentation: boolean;
  isEnableTwoFingerGestureInPresentationMode: boolean;
  isEnableFingerGestureSlideShowControl: boolean;
  isSupportWPS: boolean;
}

export interface AutomationSettings {
  isAutoFoldInEasiNote: boolean;
  isAutoFoldInEasiNoteIgnoreDesktopAnno: boolean;
  isAutoFoldInEasiCamera: boolean;
  isAutoFoldInEasiNote3C: boolean;
  isAutoFoldInSeewoPincoTeacher: boolean;
  isAutoFoldInHiteTouchPro: boolean;
  isAutoFoldInHiteCamera: boolean;
  isAutoFoldInWxBoardMain: boolean;
  isAutoFoldInOldZyBoard: boolean;
  isAutoFoldInMSWhiteboard: boolean;
  isAutoFoldInPPTSlideShow: boolean;
  isAutoKillPptService: boolean;
  isAutoKillEasiNote: boolean;
  isSaveScreenshotsInDateFolders: boolean;
  isAutoSaveStrokesAtScreenshot: boolean;
  isAutoSaveStrokesAtClear: boolean;
  isAutoClearWhenExitingWritingMode: boolean;
  minimumAutomationStrokeNumber: number;
  autoSavedStrokesLocation: string;
  autoDelSavedFiles: boolean;
  autoDelSavedFilesDaysThreshold: number;
}

export interface InkToShapeSettings {
  isInkToShapeEnabled: boolean;
  // 识别门控与对齐（PascalCase 键）：450ms 保持、5 度对齐角、多边形/圆/立方体/坐标轴。
  HoldMilliseconds: number;
  AutoAlign: boolean;
  AlignmentAngle: number;
  RecognizePolygons: boolean;
  RecognizeRoundShapes: boolean;
  RecognizeCube: boolean;
  RecognizeAxes: boolean;
}

export interface StartupSettings {
  isAutoUpdate: boolean;
  isAutoUpdateWithSilence: boolean;
  isAutoUpdateWithSilenceStartTime: string;
  isAutoUpdateWithSilenceEndTime: string;
  isEnableNibMode: boolean;
  isFoldAtStartup: boolean;
}

export interface RandSettings {
  peopleCount: number;
  isNotRepeatName: boolean;
  // 基线演进：抽样模式（0=软冷却 1=均匀 2=不回放轮抽）与冷却参数（写入侧夹取 0.25..3650 / 0.1..10）。
  samplingMode: number;
  cooldownHalfLifeDays: number;
  cooldownPenaltyStrength: number;
}

// 墨韵笔刷参数（InkWashBrushOptions：Dryness/Flatness [0,1]，Angle 度）。
export interface InkWashOptions {
  Dryness: number;
  Angle: number;
  Flatness: number;
}

export interface DisplaySettings {
  allDisplays: boolean;
  enabledDevices: string[];
}

export interface StorageSettings {
  AutomaticallyCleanCache: boolean;
  UpdateCacheDays: number;
}

export interface AiSettings {
  enableLocalCorrection: boolean;
  enableEditablePaste: boolean;
  enableVerification: boolean;
  enableTwoStage: boolean;
  teachingMode: string;
  hasSeenLassoHint: boolean;
}

// 课堂工具箱配置（新字段只追加；-2147483648 = 未保存，默认居中）。
export interface ClassroomSettings {
  version: number;
  presetId: string;
  countdownMinutes: number;
  toolkitCollapsed: boolean;
  practicePageName: string;
  customPresets: string;
  groupSplitMode: string;
  groupValueNote?: string;
  groupSplitValue: number;
  spotlightRegions: string;
  toolkitX: number;
  toolkitY: number;
  magnifierX: number;
  magnifierY: number;
}

// 第一轮功能区开关（默认全开）。
export interface FeatureSettings {
  enableAiTeaching: boolean;
  enableExerciseFlow: boolean;
  enableExampleLibrary: boolean;
  enableResourceBundle: boolean;
}

// 基线演进：实验性屏幕内容跟踪（ScreenTracking）设置组；键名 PascalCase（C# 默认序列化名）。
export interface ExperimentalSettings {
  ScreenContentTracking: boolean;
  FollowScroll: boolean;
  FollowZoom: boolean;
  MaskFixedRegions: boolean;
  RememberPages: boolean;
  PreserveUnchangedRegions: boolean;
  Word: boolean;
  PowerPoint: boolean;
  Edge: boolean;
}

export interface AppSettings {
  advanced: AdvancedSettings;
  appearance: AppearanceSettings;
  automation: AutomationSettings;
  behavior: PowerPointSettings;
  canvas: CanvasSettings;
  experimental: ExperimentalSettings;
  gesture: GestureSettings;
  inkToShape: InkToShapeSettings;
  startup: StartupSettings;
  randSettings: RandSettings;
  display: DisplaySettings;
  storage: StorageSettings;
  ai: AiSettings;
  classroom: ClassroomSettings;
  features: FeatureSettings;
}

export function createDefaultSettings(): AppSettings {
  return {
    advanced: {
      isSpecialScreen: false,
      isQuadIR: false,
      touchMultiplier: 0.25,
      nibModeBoundsWidth: 10,
      fingerModeBoundsWidth: 30,
      nibModeBoundsWidthThresholdValue: 2.5,
      fingerModeBoundsWidthThresholdValue: 2.5,
      nibModeBoundsWidthEraserSize: 0.8,
      fingerModeBoundsWidthEraserSize: 0.8,
      isEnableEdgeGestureUtil: false,
      isLogEnabled: true,
      isSecondConfimeWhenShutdownApp: false
    },
    appearance: {
      isEnableDisPlayFloatBarText: false,
      isEnableDisPlayNibModeToggler: true,
      isColorfulViewboxFloatingBar: false,
      enableViewboxFloatingBarScaleTransform: 100.0,
      enableViewboxBlackBoardScaleTransform: 100.0,
      floatingBarBottomMargin: 100.0,
      isTransparentButtonBackground: true,
      isIconRegularStyle: false,
      isShowExitButton: true,
      isShowEraserButton: true,
      isShowHideControlButton: false,
      isShowLRSwitchButton: false,
      isShowModeFingerToggleSwitch: true,
      theme: 0
    },
    automation: {
      isAutoFoldInEasiNote: false,
      isAutoFoldInEasiNoteIgnoreDesktopAnno: false,
      isAutoFoldInEasiCamera: false,
      isAutoFoldInEasiNote3C: false,
      isAutoFoldInSeewoPincoTeacher: false,
      isAutoFoldInHiteTouchPro: false,
      isAutoFoldInHiteCamera: false,
      isAutoFoldInWxBoardMain: false,
      isAutoFoldInOldZyBoard: false,
      isAutoFoldInMSWhiteboard: false,
      isAutoFoldInPPTSlideShow: false,
      isAutoKillPptService: false,
      isAutoKillEasiNote: false,
      isSaveScreenshotsInDateFolders: false,
      isAutoSaveStrokesAtScreenshot: false,
      isAutoSaveStrokesAtClear: false,
      isAutoClearWhenExitingWritingMode: false,
      minimumAutomationStrokeNumber: 0,
      autoSavedStrokesLocation: 'D:\\Ink Canvas',
      autoDelSavedFiles: false,
      autoDelSavedFilesDaysThreshold: 15
    },
    behavior: {
      isShowPPTNavigationBottom: true,
      isShowPPTNavigationSides: true,
      isShowBottomPPTNavigationPanel: true,
      isShowSidePPTNavigationPanel: true,
      powerPointSupport: true,
      isShowCanvasAtNewSlideShow: true,
      isNoClearStrokeOnSelectWhenInPowerPoint: true,
      isShowStrokeOnSelectInPowerPoint: false,
      hideStrokesAfterLeavingAnnotation: false,
      isAutoSaveStrokesInPowerPoint: true,
      isAutoSaveScreenShotInPowerPoint: false,
      isNotifyPreviousPage: false,
      isNotifyHiddenPage: true,
      isNotifyAutoPlayPresentation: true,
      isEnableTwoFingerGestureInPresentationMode: false,
      isEnableFingerGestureSlideShowControl: true,
      isSupportWPS: true
    },
    canvas: {
      InkWashWidth: 30,
      InkWash: { Dryness: 0.42, Angle: 44, Flatness: 0.44 },
      UseHardwarePressure: true,
      UsePenTilt: true,
      inkWidth: 2.5,
      highlighterWidth: null,
      inkAlpha: 140,
      isCompressPicturesUploaded: false,
      isShowCursor: false,
      inkStyle: 0,
      eraserSize: 2,
      eraserType: 0,
      hideStrokeWhenSelecting: true,
      usingWhiteboard: false,
      penNib: 0
    },
    gesture: {
      PenHasPriority: true,
      PalmErases: true,
      isEnableMultiTouchMode: true,
      isEnableTwoFingerZoom: true,
      isEnableTwoFingerTranslate: true,
      AutoSwitchTwoFingerGesture: true,
      isEnableTwoFingerRotation: false,
      isEnableTwoFingerRotationOnSelection: false,
      matrixTransformCenterPoint: 0,
      isEnableStylusGesture: true
    },
    inkToShape: {
      isInkToShapeEnabled: true,
      HoldMilliseconds: 450,
      AutoAlign: true,
      AlignmentAngle: 5,
      RecognizePolygons: true,
      RecognizeRoundShapes: true,
      RecognizeCube: true,
      RecognizeAxes: true
    },
    startup: {
      isAutoUpdate: true,
      isAutoUpdateWithSilence: false,
      isAutoUpdateWithSilenceStartTime: '00:00',
      isAutoUpdateWithSilenceEndTime: '00:00',
      isEnableNibMode: false,
      isFoldAtStartup: false
    },
    randSettings: {
      peopleCount: 60,
      isNotRepeatName: false,
      samplingMode: 0,
      cooldownHalfLifeDays: 10,
      cooldownPenaltyStrength: 1
    },
    experimental: {
      ScreenContentTracking: false,
      FollowScroll: true,
      FollowZoom: true,
      MaskFixedRegions: true,
      RememberPages: true,
      PreserveUnchangedRegions: true,
      Word: true,
      PowerPoint: true,
      Edge: true
    },
    display: {
      allDisplays: true,
      enabledDevices: []
    },
    storage: {
      AutomaticallyCleanCache: true,
      UpdateCacheDays: 7
    },
    ai: {
      enableLocalCorrection: true,
      enableEditablePaste: true,
      enableVerification: true,
      enableTwoStage: true,
      teachingMode: 'step',
      hasSeenLassoHint: false
    },
    classroom: {
      version: 1,
      presetId: 'teach',
      countdownMinutes: 3,
      toolkitCollapsed: false,
      practicePageName: '随堂练习',
      customPresets: '',
      groupSplitMode: 'groupCount',
      groupSplitValue: 4,
      spotlightRegions: '',
      toolkitX: -2147483648,
      toolkitY: -2147483648,
      magnifierX: -2147483648,
      magnifierY: -2147483648
    },
    features: {
      enableAiTeaching: true,
      enableExerciseFlow: true,
      enableExampleLibrary: true,
      enableResourceBundle: true
    }
  };
}

// @parity src/InkCanvas.Core/Settings/AppSettings.cs（ResetToRecommendation —「重置设置」推荐默认集）
// 上游 SetSettingsToRecommendation() 1:1：就地改写（保持 AutoSaveService/Palm 持有的引用有效），
// 调用方随后重新应用并保存。上游保留自动删除配置；未列字段回落构造默认值。
export function resetToRecommendation(settings: AppSettings): void {
  const autoDelSavedFiles = settings.automation.autoDelSavedFiles;
  const autoDelDaysThreshold = settings.automation.autoDelSavedFilesDaysThreshold;

  settings.advanced.isSpecialScreen = true;
  settings.advanced.isQuadIR = false;
  settings.advanced.touchMultiplier = 0.3;
  settings.advanced.nibModeBoundsWidth = 5;
  settings.advanced.fingerModeBoundsWidth = 20;
  settings.advanced.nibModeBoundsWidthThresholdValue = 2.5;
  settings.advanced.fingerModeBoundsWidthThresholdValue = 2.5;
  settings.advanced.nibModeBoundsWidthEraserSize = 0.8;
  settings.advanced.fingerModeBoundsWidthEraserSize = 0.8;
  settings.advanced.isLogEnabled = true;
  settings.advanced.isSecondConfimeWhenShutdownApp = false;
  settings.advanced.isEnableEdgeGestureUtil = false;

  settings.appearance.isEnableDisPlayFloatBarText = false;
  settings.appearance.isEnableDisPlayNibModeToggler = false;
  settings.appearance.isColorfulViewboxFloatingBar = false;
  settings.appearance.enableViewboxFloatingBarScaleTransform = 100.0;
  settings.appearance.enableViewboxBlackBoardScaleTransform = 100.0;
  settings.appearance.floatingBarBottomMargin = 100.0;
  settings.appearance.isTransparentButtonBackground = true;
  settings.appearance.isShowExitButton = true;
  settings.appearance.isShowEraserButton = true;
  settings.appearance.isShowHideControlButton = false;
  settings.appearance.isShowLRSwitchButton = false;
  settings.appearance.isShowModeFingerToggleSwitch = true;
  settings.appearance.theme = 0;

  settings.automation.isAutoFoldInEasiNote = true;
  settings.automation.isAutoFoldInEasiNoteIgnoreDesktopAnno = true;
  settings.automation.isAutoFoldInEasiCamera = true;
  settings.automation.isAutoFoldInEasiNote3C = false;
  settings.automation.isAutoFoldInSeewoPincoTeacher = false;
  settings.automation.isAutoFoldInHiteTouchPro = false;
  settings.automation.isAutoFoldInHiteCamera = false;
  settings.automation.isAutoFoldInWxBoardMain = false;
  settings.automation.isAutoFoldInOldZyBoard = false;
  settings.automation.isAutoFoldInMSWhiteboard = false;
  settings.automation.isAutoFoldInPPTSlideShow = false;
  settings.automation.isAutoKillPptService = false;
  settings.automation.isAutoKillEasiNote = false;
  settings.automation.isSaveScreenshotsInDateFolders = false;
  settings.automation.isAutoSaveStrokesAtScreenshot = true;
  settings.automation.isAutoSaveStrokesAtClear = true;
  settings.automation.isAutoClearWhenExitingWritingMode = false;
  settings.automation.minimumAutomationStrokeNumber = 0;
  settings.automation.autoSavedStrokesLocation = 'D:\\Ink Canvas';
  settings.automation.autoDelSavedFiles = autoDelSavedFiles;
  settings.automation.autoDelSavedFilesDaysThreshold = autoDelDaysThreshold;

  settings.behavior.isShowPPTNavigationBottom = false;
  settings.behavior.isShowPPTNavigationSides = true;
  settings.behavior.isShowBottomPPTNavigationPanel = true;
  settings.behavior.isShowSidePPTNavigationPanel = true;
  settings.behavior.powerPointSupport = true;
  settings.behavior.isShowCanvasAtNewSlideShow = true;
  settings.behavior.isNoClearStrokeOnSelectWhenInPowerPoint = true;
  settings.behavior.isShowStrokeOnSelectInPowerPoint = false;
  settings.behavior.isAutoSaveStrokesInPowerPoint = true;
  settings.behavior.isAutoSaveScreenShotInPowerPoint = true;
  settings.behavior.isNotifyPreviousPage = false;
  settings.behavior.isNotifyHiddenPage = false;
  settings.behavior.isNotifyAutoPlayPresentation = true;
  settings.behavior.isEnableTwoFingerGestureInPresentationMode = false;
  settings.behavior.isEnableFingerGestureSlideShowControl = false;
  settings.behavior.isSupportWPS = true;

  settings.canvas.inkWidth = 2.5;
  settings.canvas.highlighterWidth = null;
  settings.canvas.inkAlpha = 80;
  settings.canvas.isCompressPicturesUploaded = false;
  settings.canvas.isShowCursor = false;
  settings.canvas.inkStyle = 0;
  settings.canvas.penNib = 0;
  settings.canvas.eraserSize = 1;
  settings.canvas.eraserType = 0;
  settings.canvas.hideStrokeWhenSelecting = false;
  settings.canvas.usingWhiteboard = false;

  settings.gesture.matrixTransformCenterPoint = 0;
  settings.gesture.AutoSwitchTwoFingerGesture = true;
  settings.gesture.isEnableTwoFingerTranslate = true;
  settings.gesture.isEnableTwoFingerZoom = false;
  settings.gesture.isEnableTwoFingerRotation = false;
  settings.gesture.isEnableTwoFingerRotationOnSelection = true;
  settings.gesture.isEnableMultiTouchMode = true;
  settings.gesture.isEnableStylusGesture = true;

  settings.inkToShape.isInkToShapeEnabled = true;

  settings.startup.isEnableNibMode = false;
  settings.startup.isAutoUpdate = true;
  settings.startup.isAutoUpdateWithSilence = true;
  settings.startup.isAutoUpdateWithSilenceStartTime = '18:20';
  settings.startup.isAutoUpdateWithSilenceEndTime = '07:40';
  settings.startup.isFoldAtStartup = false;

  settings.randSettings.peopleCount = 60;
  settings.randSettings.isNotRepeatName = false;
}
