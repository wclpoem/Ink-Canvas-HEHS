using InkCanvas.App.Services;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.App.Views;

public sealed partial class SettingsWindow
{
    private void BuildGeneralSettings()
    {
        AddSection("启动行为");
        _content.Children.Add(ToggleRow("开机时运行", () => Platform.System.AutoStartService.IsEnabled(),
            v => Platform.System.AutoStartService.SetEnabled(v, Environment.ProcessPath ?? "InkCanvas.exe")));
        _content.Children.Add(ToggleRow("开机运行后收纳到侧边栏", () => S.Startup.IsFoldAtStartup,
            v => S.Startup.IsFoldAtStartup = v, note: "下次启动时生效。"));
        _content.Children.Add(ToggleRow("关闭软件时二次弹窗确认", () => S.Advanced.IsSecondConfimeWhenShutdownApp,
            value => S.Advanced.IsSecondConfimeWhenShutdownApp = value));
        AddSection("课堂应用兼容");
        _content.Children.Add(ToggleRow("自动查杀希沃“PPT 小工具”", () => S.Automation.IsAutoKillPptService, value => S.Automation.IsAutoKillPptService = value,
            note: "每隔 10 秒关闭当前用户的 PPTService 和 SeewoIwbAssistant 进程。"));
        _content.Children.Add(ToggleRow("自动查杀 希沃白板5", () => S.Automation.IsAutoKillEasiNote, value => S.Automation.IsAutoKillEasiNote = value,
            note: "每隔 10 秒强制关闭当前用户的 EasiNote 进程，未保存的内容会丢失。"));
        AddSection("应用管理");
        _content.Children.Add(BuildTopCard());
    }

    private void BuildWritingSettings()
    {
        AddSection("书写与笔尖");
        _content.Children.Add(ToggleRow("使用硬件压感", () => S.Canvas.UseHardwarePressure,
            v => S.Canvas.UseHardwarePressure = v, note: "压感笔按实际压力控制各笔刷与荧光笔的粗细，单击描点也随压力变化。红外触摸屏使用笔速模拟压感。"));
        _content.Children.Add(ToggleRow("倾斜笔尖与水墨阴影", () => S.Canvas.UsePenTilt,
            v => S.Canvas.UsePenTilt = v));
        _content.Children.Add(ComboRow("笔迹风格", OriginalMenuCatalog.PenStyles,
            () => S.Canvas.InkStyle, i => S.Canvas.InkStyle = i, index => new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 12,
                Children = { new PenStyleIcon(index) { Width = 64, Height = 26 },
                    new TextBlock { Text = OriginalMenuCatalog.PenStyles[index],
                        VerticalAlignment = VerticalAlignment.Center } },
            }));
        _content.Children.Add(ComboRow("笔尖", OriginalMenuCatalog.PenNibs,
            () => (int)S.Canvas.PenNib, i => S.Canvas.PenNib = (InkCanvas.Core.Ink.PenNib)i));
        _content.Children.Add(SliderRow("画笔粗细（原菜单 1–30；设置值为菜单值的一半）",
            () => S.Canvas.InkWidth, 0.5, 15, v => S.Canvas.InkWidth = v, 0.5));
        _content.Children.Add(ToggleRow("显示画笔光标", () => S.Canvas.IsShowCursor, value => S.Canvas.IsShowCursor = value));
        AddSection("荧光笔");
        _content.Children.Add(SliderRow("荧光笔透明度", () => S.Canvas.InkAlpha,
            20, 240, v => S.Canvas.InkAlpha = (int)v, 20));
        _content.Children.Add(SliderRow("荧光笔粗细", () => (S.Canvas.HighlighterWidth ?? S.Canvas.InkWidth) * 2,
            1, OriginalMenuCatalog.HighlighterWidthMaximum, v => S.Canvas.HighlighterWidth = v / 2));
        AddSection("水墨笔刷");
        _content.Children.Add(SliderRow("水墨笔锋粗细（px）", () => S.Canvas.InkWashWidth,
            1, 120, value => S.Canvas.InkWashWidth = value));
        _content.Children.Add(SliderRow("水墨枯笔飞白（%）", () => Math.Round(S.Canvas.InkWash.Dryness * 100),
            0, 100, value => S.Canvas.InkWash = S.Canvas.InkWash with { Dryness = (float)(value / 100) }));
        _content.Children.Add(SliderRow("水墨侧锋倾角（°）", () => S.Canvas.InkWash.Angle,
            0, 180, value => S.Canvas.InkWash = S.Canvas.InkWash with { Angle = (float)value }));
        _content.Children.Add(SliderRow("水墨扁笔侧锋度（%）", () => Math.Round(S.Canvas.InkWash.Flatness * 100),
            0, 90, value => S.Canvas.InkWash = S.Canvas.InkWash with { Flatness = (float)(value / 100) }));
        AddSection("橡皮");
        _content.Children.Add(ComboRow("橡皮大小", OriginalMenuCatalog.EraserSizes,
            () => S.Canvas.EraserSize, i => S.Canvas.EraserSize = i));
        _content.Children.Add(ComboRow("橡皮模式", OriginalMenuCatalog.EraserModes,
            () => S.Canvas.EraserType, i => S.Canvas.EraserType = i));
    }

    private void BuildCanvasSettings()
    {
        AddSection("画布与图片");
        _content.Children.Add(ToggleRow("上传图片时将画质压缩至 1080P 及以下", () => S.Canvas.IsCompressPicturesUploaded,
            v => S.Canvas.IsCompressPicturesUploaded = v));
        _content.Children.Add(ToggleRow("退出画板模式后隐藏墨迹", () => S.Canvas.HideStrokeWhenSelecting,
            v => S.Canvas.HideStrokeWhenSelecting = v,
            note: "屏幕批注切换到鼠标模式后的墨迹显示。PPT 放映在“演示与课件”中单独设置。"));
        _content.Children.Add(ToggleRow("白板背景配色", () => S.Canvas.UsingWhiteboard,
            v =>
            {
                S.Canvas.UsingWhiteboard = v;
                _services.Board.SetBackground(v ? InkCanvas.Features.Board.BoardBackground.White : InkCanvas.Features.Board.BoardBackground.Black);
            }));
        _content.Children.Add(ToggleRow("切换到鼠标模式后自动清屏", () => S.Automation.IsAutoClearWhenExitingWritingMode,
            value => S.Automation.IsAutoClearWhenExitingWritingMode = value, note: "清除桌面当前页批注，可通过撤销恢复；PPT 可单独设置保留墨迹。"));
        AddSection("停笔成形");
        _content.Children.Add(ToggleRow("启用墨迹识别", () => S.InkToShape.IsInkToShapeEnabled,
            v => S.InkToShape.IsInkToShapeEnabled = v));
        _content.Children.Add(SliderRow("停笔吸附等待时间 (毫秒)", () => S.InkToShape.HoldMilliseconds,
            300, 1500, v => S.InkToShape.HoldMilliseconds = (int)v, 50));
        _content.Children.Add(ToggleRow("识别圆与椭圆", () => S.InkToShape.RecognizeRoundShapes,
            v => S.InkToShape.RecognizeRoundShapes = v));
        _content.Children.Add(ToggleRow("识别三角形、矩形与平行四边形", () => S.InkToShape.RecognizePolygons,
            v => S.InkToShape.RecognizePolygons = v));
        _content.Children.Add(ToggleRow("识别立方体线框", () => S.InkToShape.RecognizeCube,
            v => S.InkToShape.RecognizeCube = v));
        _content.Children.Add(ToggleRow("识别坐标轴", () => S.InkToShape.RecognizeAxes,
            v => S.InkToShape.RecognizeAxes = v));
        AddSection("自动对齐");
        _content.Children.Add(ToggleRow("自动对齐水平、垂直与 45° 方向", () => S.InkToShape.AutoAlign,
            v => S.InkToShape.AutoAlign = v, note: "停笔吸附后，继续拖动可微调端点或顶点，抬笔确认。撤销可恢复原笔迹。"));
        _content.Children.Add(SliderRow("方向吸附容差 (度)", () => S.InkToShape.AlignmentAngle,
            1, 12, v => S.InkToShape.AlignmentAngle = v, 1));
    }

    private void BuildTouchSettings()
    {
        AddSection("触控与变换");
        _content.Children.Add(ToggleRow("压感笔书写优先", () => S.Gesture.PenHasPriority,
            v => S.Gesture.PenHasPriority = v, note: "笔尖落下后抑制同一屏幕上的手掌和手指输入，其他屏幕仍可同时书写。"));
        _content.Children.Add(ToggleRow("手掌与大面积接触擦除", () => S.Gesture.PalmErases,
            v => S.Gesture.PalmErases = v, note: "未使用压感笔时，大面积接触作为板擦。阈值使用本页“触控屏适配”和“触摸判定参数”中的设置。"));
        _content.Children.Add(ComboRow("缩放/旋转变换中心点", new[]
            {
                "画布中心点（默认）", "全部内容的中心点", "手势操作的中心点",
            }, () => (int)S.Gesture.MatrixTransformCenterPoint,
            i => S.Gesture.MatrixTransformCenterPoint = (MatrixTransformCenterPointOptions)i));
        _content.Children.Add(Require(ToggleRow("自动开关两指缩放功能", () => S.Gesture.AutoSwitchTwoFingerGesture,
            value => S.Gesture.AutoSwitchTwoFingerGesture = value,
            note: "进入白板时临时关闭多指书写并启用双指平移，离开时恢复原手势设置。"), TouchRequirement));
        _content.Children.Add(ToggleRow("允许双指旋转选中的墨迹", () => S.Gesture.IsEnableTwoFingerRotationOnSelection,
            v => S.Gesture.IsEnableTwoFingerRotationOnSelection = v,
            note: "支持在多指书写模式下旋转选中的墨迹、形状、图片和视频。选区捏合缩放使用两指中心。"));
        _content.Children.Add(ToggleRow("启用多点触控模式", () => S.Gesture.IsEnableMultiTouchMode,
            v => S.Gesture.IsEnableMultiTouchMode = v));
        AddSection("双指手势");
        _content.Children.Add(ToggleRow("双指缩放", () => S.Gesture.IsEnableTwoFingerZoom,
            v => S.Gesture.IsEnableTwoFingerZoom = v));
        _content.Children.Add(ToggleRow("双指平移", () => S.Gesture.IsEnableTwoFingerTranslate,
            v => S.Gesture.IsEnableTwoFingerTranslate = v));
        _content.Children.Add(ToggleRow("双指旋转", () => S.Gesture.IsEnableTwoFingerRotation,
            v => S.Gesture.IsEnableTwoFingerRotation = v));
        AddSection("触控屏适配");
        _content.Children.Add(ToggleRow("启用笔尖模式", () => S.Startup.IsEnableNibMode,
            v => S.Startup.IsEnableNibMode = v));
        _content.Children.Add(InfoRow("# 启用特殊屏幕模式以调节触控屏触摸大小倍数。# 触控屏触摸大小倍数调整为 0 即为禁用手势橡皮。"));
        _content.Children.Add(ToggleRow("特殊屏幕模式", () => S.Advanced.IsSpecialScreen,
            v => S.Advanced.IsSpecialScreen = v));
        _content.Children.Add(SliderRow("特殊屏幕触摸大小倍数", () => S.Advanced.TouchMultiplier,
            0, 5, v => S.Advanced.TouchMultiplier = v, 0.01));
        _content.Children.Add(Require(ButtonRow("触摸大小倍数校准", "在校准区域用触控笔点按，按接触尺寸计算触摸大小倍数。",
            async () => await CalibrateTouchAsync(), "开始校准"), () => TouchRequirement()
                ?? (S.Advanced.IsSpecialScreen ? null : "请先启用特殊屏幕模式。")));
        AddSection("触摸判定参数");
        _content.Children.Add(SliderRow("笔尖模式 BoundsWidth", () => S.Advanced.NibModeBoundsWidth,
            1, 50, v => S.Advanced.NibModeBoundsWidth = (int)v));
        _content.Children.Add(SliderRow("手指模式 BoundsWidth", () => S.Advanced.FingerModeBoundsWidth,
            1, 50, v => S.Advanced.FingerModeBoundsWidth = (int)v));
        _content.Children.Add(SliderRow("笔尖模式 ThresholdValue", () => S.Advanced.NibModeBoundsWidthThresholdValue,
            0, 10, v => S.Advanced.NibModeBoundsWidthThresholdValue = v, 0.01));
        _content.Children.Add(SliderRow("手指模式 ThresholdValue", () => S.Advanced.FingerModeBoundsWidthThresholdValue,
            0, 10, v => S.Advanced.FingerModeBoundsWidthThresholdValue = v, 0.01));
        _content.Children.Add(SliderRow("笔尖模式 EraserSize", () => S.Advanced.NibModeBoundsWidthEraserSize,
            0.01, 10, v => S.Advanced.NibModeBoundsWidthEraserSize = v, 0.01));
        _content.Children.Add(SliderRow("手指模式 EraserSize", () => S.Advanced.FingerModeBoundsWidthEraserSize,
            0.01, 10, v => S.Advanced.FingerModeBoundsWidthEraserSize = v, 0.01));
        _content.Children.Add(ToggleRow("四边红外模式", () => S.Advanced.IsQuadIR,
            v => S.Advanced.IsQuadIR = v, note: "以宽高几何平均计算接触尺寸。"));
        _content.Children.Add(ToggleRow("阻止 Win 10/11 的边缘手势", () => S.Advanced.IsEnableEdgeGestureUtil,
            v => S.Advanced.IsEnableEdgeGestureUtil = v));
        _content.Children.Add(InfoRow("触摸参数根据驱动报告的接触尺寸生效，可先使用校准区域测量。"));
        AddSection("多显示器书写");
        BuildDisplaySettings();
    }

    private void BuildAppearanceSettings()
    {
        AddSection("主题与工具栏");
        int[] themeMap = { 1, 2, 0 };
        _content.Children.Add(ComboRow("颜色主题", OriginalMenuCatalog.ThemeOptions,
            () => Array.IndexOf(themeMap, S.Appearance.Theme), i => S.Appearance.Theme = themeMap[i]));
        _content.Children.Add(ToggleRow("浮动工具栏显示文字注释", () => S.Appearance.IsEnableDisPlayFloatBarText,
            v => S.Appearance.IsEnableDisPlayFloatBarText = v));
        _content.Children.Add(ToggleRow("浮动工具栏背景色", () => S.Appearance.IsColorfulViewboxFloatingBar,
            v => S.Appearance.IsColorfulViewboxFloatingBar = v));
        AddSection("大小与位置");
        _content.Children.Add(SliderRow("浮动工具栏底部边距 (像素)", () => S.Appearance.FloatingBarBottomMargin,
            0, 200, v => S.Appearance.FloatingBarBottomMargin = v, presets: new[] { 100d },
            presetsLabel: "底部边距快捷值", formatPreset: v => $"重置为 {v:0}"));
        _content.Children.Add(SliderRow("浮动工具栏 UI 缩放 (%)", () => S.Appearance.FloatingBarScale,
            50, 150, v => S.Appearance.FloatingBarScale = v, presets: new[] { 80d, 90d, 100d, 110d, 125d }, presetsLabel: "工具栏缩放快捷值"));
        _content.Children.Add(SliderRow("画板 UI 缩放 (%)", () => S.Appearance.BlackboardScale,
            50, 150, v => S.Appearance.BlackboardScale = v, presets: new[] { 80d, 90d, 100d, 110d, 125d }, presetsLabel: "画板缩放快捷值"));
        AddSection("显示的按钮");
        _content.Children.Add(ToggleRow("按钮背景透明", () => S.Appearance.IsTransparentButtonBackground,
            v => S.Appearance.IsTransparentButtonBackground = v));
        _content.Children.Add(ToggleRow("图标使用 Regular 线性风格", () => S.Appearance.IsIconRegularStyle,
            v => S.Appearance.IsIconRegularStyle = v));
        _content.Children.Add(ToggleRow("显示退出按钮", () => S.Appearance.IsShowExitButton,
            v => S.Appearance.IsShowExitButton = v));
        _content.Children.Add(ToggleRow("显示橡皮按钮", () => S.Appearance.IsShowEraserButton,
            v => S.Appearance.IsShowEraserButton = v));
        _content.Children.Add(ToggleRow("显示隐藏控制按钮", () => S.Appearance.IsShowHideControlButton,
            v => S.Appearance.IsShowHideControlButton = v));
        _content.Children.Add(ToggleRow("显示左右切换按钮", () => S.Appearance.IsShowLRSwitchButton,
            v => S.Appearance.IsShowLRSwitchButton = v));
        _content.Children.Add(ToggleRow("显示手指模式切换按钮", () => S.Appearance.IsShowModeFingerToggleSwitch,
            v => S.Appearance.IsShowModeFingerToggleSwitch = v));
        _content.Children.Add(ToggleRow("在调色盘窗口中显示 启用笔尖模式 切换按钮", () => S.Appearance.IsEnableDisPlayNibModeToggler,
            value => S.Appearance.IsEnableDisPlayNibModeToggler = value));
        AddSection("工具栏自动收纳");
        _content.Children.Add(ToggleRow("进入“希沃白板”时自动收纳", () => S.Automation.IsAutoFoldInEasiNote,
            v => S.Automation.IsAutoFoldInEasiNote = v));
        _content.Children.Add(Require(ToggleRow("自动收纳忽略桌面EN批注窗口", () => S.Automation.IsAutoFoldInEasiNoteIgnoreDesktopAnno,
            value => S.Automation.IsAutoFoldInEasiNoteIgnoreDesktopAnno = value,
            note: "应用于希沃白板的空标题小型桌面批注窗口。"), () =>
                S.Automation.IsAutoFoldInEasiNote ? null : "请先启用希沃白板自动收纳。"));
        _content.Children.Add(ToggleRow("进入“希沃视频展台”时自动收纳", () => S.Automation.IsAutoFoldInEasiCamera,
            v => S.Automation.IsAutoFoldInEasiCamera = v));
        _content.Children.Add(ToggleRow("进入“希沃轻白板”时自动收纳", () => S.Automation.IsAutoFoldInEasiNote3C,
            value => S.Automation.IsAutoFoldInEasiNote3C = value));
        _content.Children.Add(ToggleRow("进入“希沃品课”时自动收纳", () => S.Automation.IsAutoFoldInSeewoPincoTeacher,
            v => S.Automation.IsAutoFoldInSeewoPincoTeacher = v));
        _content.Children.Add(ToggleRow("进入“鸿合白板”时自动收纳", () => S.Automation.IsAutoFoldInHiteTouchPro,
            v => S.Automation.IsAutoFoldInHiteTouchPro = v));
        _content.Children.Add(ToggleRow("进入“鸿合视频展台”时自动收纳", () => S.Automation.IsAutoFoldInHiteCamera, value => S.Automation.IsAutoFoldInHiteCamera = value));
        _content.Children.Add(ToggleRow("进入“文香白板”时自动收纳", () => S.Automation.IsAutoFoldInWxBoardMain, value => S.Automation.IsAutoFoldInWxBoardMain = value));
        _content.Children.Add(ToggleRow("进入“中原旧白板”时自动收纳", () => S.Automation.IsAutoFoldInOldZyBoard, value => S.Automation.IsAutoFoldInOldZyBoard = value));
        _content.Children.Add(ToggleRow("进入“微软 Whiteboard”时自动收纳", () => S.Automation.IsAutoFoldInMSWhiteboard,
            v => S.Automation.IsAutoFoldInMSWhiteboard = v));
    }

    private void BuildPresentationSettings()
    {
        AddSection("翻页控件");
        _content.Children.Add(ToggleRow("显示 PPT 下方翻页箭头按钮", () => S.PowerPoint.IsShowBottomPPTNavigationPanel,
            v => S.PowerPoint.IsShowBottomPPTNavigationPanel = v));
        _content.Children.Add(ToggleRow("显示 PPT 下方页码按钮", () => S.PowerPoint.IsShowPPTNavigationBottom,
            v => S.PowerPoint.IsShowPPTNavigationBottom = v));
        _content.Children.Add(ToggleRow("显示 PPT 侧边翻页箭头按钮", () => S.PowerPoint.IsShowSidePPTNavigationPanel,
            v => S.PowerPoint.IsShowSidePPTNavigationPanel = v));
        _content.Children.Add(ToggleRow("显示 PPT 侧边页码按钮", () => S.PowerPoint.IsShowPPTNavigationSides,
            v => S.PowerPoint.IsShowPPTNavigationSides = v));
        AddSection("放映与批注");
        _content.Children.Add(ToggleRow("Microsoft PowerPoint 支持", () => S.PowerPoint.PowerPointSupport,
            v => S.PowerPoint.PowerPointSupport = v));
        _content.Children.Add(ToggleRow("WPS 支持", () => S.PowerPoint.IsSupportWPS,
            v => { S.PowerPoint.IsSupportWPS = v; _services.Ppt.AllowWps = v; }));
        _content.Children.Add(ToggleRow("进入 PPT 放映时自动进入批注模式", () => S.PowerPoint.IsShowCanvasAtNewSlideShow,
            v => S.PowerPoint.IsShowCanvasAtNewSlideShow = v));
        UIElement hideInkInMouseMode = ToggleRow("关闭批注模式后隐藏墨迹", () => S.PowerPoint.HideStrokesAfterLeavingAnnotation,
            v => S.PowerPoint.HideStrokesAfterLeavingAnnotation = v,
            note: "切换到鼠标模式后暂时隐藏当前页墨迹，不会清除墨迹。默认保留显示。");
        if (hideInkInMouseMode is FrameworkElement row) row.Margin = new Thickness(24, 0, 0, 0);
        _content.Children.Add(hideInkInMouseMode);
        _content.Children.Add(Require(ToggleRow("允许幻灯片模式下的双指手势", () => S.PowerPoint.IsEnableTwoFingerGestureInPresentationMode,
            value => S.PowerPoint.IsEnableTwoFingerGestureInPresentationMode = value), () => PresentationRequirement() ?? TouchRequirement()));
        _content.Children.Add(ToggleRow("允许使用手指手势进行幻灯片翻页", () => S.PowerPoint.IsEnableFingerGestureSlideShowControl,
            v => S.PowerPoint.IsEnableFingerGestureSlideShowControl = v));
        _content.Children.Add(ToggleRow("进入PPT 放映时自动收纳至侧边栏", () => S.Automation.IsAutoFoldInPPTSlideShow,
            v => S.Automation.IsAutoFoldInPPTSlideShow = v));
        AddSection("保存与提示");
        _content.Children.Add(ToggleRow("自动幻灯片截屏", () => S.PowerPoint.IsAutoSaveScreenShotInPowerPoint,
            v => S.PowerPoint.IsAutoSaveScreenShotInPowerPoint = v));
        _content.Children.Add(ToggleRow("自动保存幻灯片墨迹", () => S.PowerPoint.IsAutoSaveStrokesInPowerPoint,
            v => S.PowerPoint.IsAutoSaveStrokesInPowerPoint = v));
        _content.Children.Add(ToggleRow("记忆并提示上次播放位置", () => S.PowerPoint.IsNotifyPreviousPage,
            v => S.PowerPoint.IsNotifyPreviousPage = v));
        _content.Children.Add(ToggleRow("提示隐藏幻灯片", () => S.PowerPoint.IsNotifyHiddenPage,
            v => S.PowerPoint.IsNotifyHiddenPage = v));
        _content.Children.Add(ToggleRow("提示是否已启用自动播放", () => S.PowerPoint.IsNotifyAutoPlayPresentation,
            v => S.PowerPoint.IsNotifyAutoPlayPresentation = v));
        _content.Children.Add(Require(ToggleRow("PPT 选择时不清除墨迹", () => S.PowerPoint.IsNoClearStrokeOnSelectWhenInPowerPoint,
            value => S.PowerPoint.IsNoClearStrokeOnSelectWhenInPowerPoint = value,
            note: "进入鼠标模式时保留当前幻灯片的批注。"), () => PresentationRequirement()
                ?? (S.Automation.IsAutoClearWhenExitingWritingMode ? null : "请先在“画布与识别”中启用切换到鼠标模式后自动清屏。")));
    }

    private void BuildAboutSettings()
    {
        AddSection("版本与更新");
        _content.Children.Add(InfoRow(_updater is null
            ? "Ink Canvas · WinUI 3"
            : $"当前版本：{_updater.CurrentVersion}"));
        _content.Children.Add(ButtonRow("检查更新", "从 GitHub 发行版检查新版本", async () =>
        {
            if (_updater is not null) await _updater.CheckAsync(interactive: true);
        }));
        _content.Children.Add(ButtonRow("版本列表", "查看仓库的稳定发行版和更新说明", async () =>
        { if (_updater is not null) await _updater.ShowVersionsAsync(); }));
        _updateStatus = new TextBlock { Text = _updater?.Status ?? "更新服务未启动", TextWrapping = TextWrapping.Wrap };
        _content.Children.Add(Card(_updateStatus));
        AddSection("自动更新");
        _content.Children.Add(ToggleRow("自动检查更新", () => S.Startup.IsAutoUpdate,
            v => S.Startup.IsAutoUpdate = v));
        _content.Children.Add(ToggleRow("静默更新", () => S.Startup.IsAutoUpdateWithSilence,
            v => S.Startup.IsAutoUpdateWithSilence = v,
            note: "开启后，在设定时段内等待画布空闲再自动更新。存在墨迹、文档或正在使用工具时会继续等待。"));
        _content.Children.Add(ComboRow("静默更新起始时间", SilenceTimes,
            () => Array.IndexOf(SilenceTimes, S.Startup.AutoUpdateWithSilenceStartTime),
            i => S.Startup.AutoUpdateWithSilenceStartTime = SilenceTimes[i]));
        _content.Children.Add(ComboRow("静默更新终止时间", SilenceTimes,
            () => Array.IndexOf(SilenceTimes, S.Startup.AutoUpdateWithSilenceEndTime),
            i => S.Startup.AutoUpdateWithSilenceEndTime = SilenceTimes[i]));
        _content.Children.Add(InfoRow("# 若终止时间小于起始时间，即将终止时间视为第二天的时间。若起始时间与终止时间相同，即视为全天候时间。"));
        AddSection("诊断");
        _content.Children.Add(ToggleRow("记录日志", () => S.Advanced.IsLogEnabled,
            v => S.Advanced.IsLogEnabled = v));
        AddSection("帮助");
        _content.Children.Add(ButtonRow("操作指南", "模式、工具、手势与快捷键", () => _workspace.SelectPage("操作指南")));
        _content.Children.Add(ButtonRow("更新日志", "查看版本变更记录", () =>
        {
            var w = InfoWindow.OpenChangeLog(S.Appearance.Theme);
            w.Activate();
        }));

        AddSection("开源与贡献者");
        _content.Children.Add(InfoRow("* 使用和分发本软件前，请您应当且务必知晓相关开源协议，且您应当知晓本软件基于 https://github.com/WXRIW/Ink-Canvas 修改而成。开源协议: GNU General Public License v3.0"));
        _content.Children.Add(LinkRow("https://github.com/InkCanvas/Ink-Canvas-Artistry", "本项目开发者: CJ Wu (ChangSakura)"));
        _content.Children.Add(LinkRow("https://github.com/WXRIW/Ink-Canvas", "原项目开发者: XY Wang (WXRIW)"));
        _content.Children.Add(InfoRow("原项目贡献者: Kengwang、Raspberry Kan、jiajiaxd、CN-Ironegg、Clover Yan、STBBRD、ChangSakura"));
    }
}
