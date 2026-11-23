# 移植对照与验证状态

## 多屏与透明度修复

屏幕坐标统一使用物理像素，XAML 尺寸按目标显示器 DPI 换算。批注层覆盖虚拟桌面，并保持创建时的坐标缩放，避免跨屏时整份墨迹因 DPI 改变而伸缩。布局变化时调整视图平移以保留桌面位置，重建 DIB 和渲染缓存，撤销记录保留。

工具栏初始落在鼠标所在屏幕，此后跟随用户摆放。工具栏与弹窗按工作区缩小和夹取，收纳按钮完整位于所属显示器内。白板与辅助窗口跟随来源窗口，PowerPoint 导航使用放映窗口所在屏幕。

浮动工具栏与弹窗使用带主题底色的亚克力材质。系统透明效果关闭、远程桌面或材质不可用时，直接在 XAML 卡片上绘制不透明主题色。回退路径取消整窗 `WS_EX_LAYERED` 透明度，避免 WinUI 交换链与原生透明合成产生黑色底板。浅色工具栏回退为白色，高对比度使用系统背景色，深浅色切换和隐藏恢复会重建正确的卡片画刷。

笑脸展开与折叠期间，原生窗口和 Viewbox 保持展开尺寸，按进度裁切实际卡片，结束后再调整最终窗口大小，减少逐帧重建交换链造成的闪烁。动画继续遵循系统动画开关。766 项域测试、42 项原生主题检查和 102 项展开折叠检查通过，覆盖三种模式和 50%、100%、200% 工具栏缩放。回退浅色截图保存在 `artifacts/floating-chrome-theme/toolbar-1.png`，报告分别为 `themes.json` 和 `artifacts/floating-chrome-animation/toolbar-animation.json`。当前环境可验证材质回退与原生布局，目标 Windows 11 设备的实际桌面合成表现仍需实机复核。

新增 `--displaycheck=<输出目录>`，覆盖工具栏缩放、调色盘、收纳恢复、模式切换、透明度读回、模拟虚拟桌面尺寸与负坐标原点变更、像素重绘和撤销重做。当前机器仅有 1920×1080、100% 缩放显示器，真实混合 DPI、跨屏触摸和热插拔仍需目标设备复核。

## 3.4.0 还原轮（真机部分验证）

平台级根因与修复（均有真机证据，详见 CHANGELOG 3.4.0）：overlay 点击抬升Z带埋没chrome窗口、
WinUI island 拖动三缺陷（HTCAPTION 无效/PointerMoved 停发/相对坐标）、构造期 DPI=0 致裁切、
island 与 HWND 错位、Server 2025 composition interop DWM 死锁（永久移除，半透改 DesktopAcrylicBackdrop）、
未root容器 Parent 不设置致重复父级 COMException、放置记录snap回 (100,100)。

WPF 1:1：浮动栏 ViewboxFloatingBar 结构/顺序/刷色/独立矩形/面板独立弹窗；笔/荧光笔双菜单；
图形所见即所得 24 矢量预览＋图钉/长按；形状语义（回笔/中心半径/吸附/两步体/等宽/克隆）＋21 单测；
设置 440DIP 面板＋重置推荐；计时器/抽奖/指南/是否窗重建；热键 WPF 键位；画板三浮条＋选区面板＋橡皮两步。

round 3 已落地：系统性裁切审计（所有窗在真实 DPI 下按工作区夹取、首激活延迟一 tick 落位、
Kind-0 记录防重激活snap回）；调色盘/图形popup 移除 WPF 没有的标题栏（改 FloatBarTitleBackground 标题带、
色选即关、Escape/外部点击/落笔收起、图钉跨开合持久）；色板/主题格改 Button（读屏+UIA）；
图标居中（固定 22DIP 图标区＋caption 不位移）；触摸/压感反馈抑制（overlay+画板 SetWindowFeedbackSetting）、
overlay 指针→present 全同步＋区域限定拷贝降延迟；PPT 200ms 放映轮询、导航面板折叠/画板门控、
放映开始退画板+红笔、结束退画板、翻页先退画板、WPS wpp 进程守卫、导航面板几何/glyph/计数格式 1:1；
画板元素（图片/视频）参与旋转/翻转/双指手势（累积 canvas 矩阵、单撤销单元）、选区面板精确 WPF 按钮集、
存为图片精确 padding、衍至新页、插入居中缩放 1:1、视频刷新保留播放位置；多指书写语义修正
（MultiTouchEnabled=true=多指书写且禁手势，WPF MW_TouchEvents 275-291）、选区手势枢轴读设置；
收纳把手改露出 ~14DIP 小条＋纳入 Z 重压（修复收纳后点不到恢复）＋Button 语义；
画板打开闪退修复（模板资源刷需克隆实例，COMException 0x800F0902）；指南 Shift 键帽不再换行。

headless 自检钩子：`--shot=<dir>`（各窗激活后自渲染 RenderTargetBitmap PNG）、`--open=a,b,c`（逐窗间隔开启）。
断开会话下 CopyFromScreen/PrintWindow 不可靠、输入注入被拒；UIA Invoke 可用于 Button。

仍待目标设备复核：触摸/压感硬件、Win11 亚克力真半透（Server 回退不透明）、Office/WPS COM 实机、
视频帧导出 best-effort、克隆上限提示与确认对话框为移植期刻意保留的安全 additions。

## 3.3.0 新增移植（行为未验证）

按用户要求，本轮没有启动应用、没有接真实触摸屏/压感笔/PowerPoint/WPS；仅做 Release build compile-check。例外：为了修复同一笔自交镂空运行了 3 项纯几何回归测试（3/3 通过），但 Win2D 实际栅格化行为未测试。详细来源和差异见 `board-porting-notes.md`、`ppt-porting-notes.md`、`menu-porting-notes.md`。

- 鼠标模式现在是独立鼠标指针矢量图标，不再用锁图标；选笔/荧光笔/图形会恢复标注模式。
- 独立画笔/荧光笔/图形选择窗口，避免 WinUI Flyout 被浮动栏窗口边界裁切。
- 恢复上游 Segoe Fluent Icons 字体文件，避免外部客户端字体缺失。
- DPI：浮动栏按 `GetDpiForWindow` 换算 DIP/物理像素，窗口 DPI 变更后按当前位置重新布局；弹出菜单按锚点与监视器 DPI 夹取。
- 拖动：浮动栏背景发 `WM_NCLBUTTONDOWN/HTCAPTION` 使用系统原生拖动循环，避免逐 pointer move 手动改 `AppWindow` 导致抖动。交互按钮通过 ancestor hit-test 排除，不应触发拖动。
- 圆角/阴影：对窗口设置 `DWMWA_WINDOW_CORNER_PREFERENCE`，不再调用 `DwmExtendFrameIntoClientArea`，避免向不透明 XAML 内容扩展玻璃帧形成白圈。外观在目标 Windows 版本未验证。
- 自交笔画：中心线每段转为同向 capsule contour，以 Win2D `Winding` geometry group 合并；同一笔自交不挖洞，半透明同一笔只做一次合成。新增 3 个 Core 几何单测，但按本轮指令未运行。
- 白板重建底部三栏工具、页导航、元素图片、保存/加载与独立分页； `BoardWindow.CurrentPageChanged` 在 surface 重绑后发出。Shell 构造传 `new BoardWindow(Services, this)`，关闭回调恢复 overlay，并将浮动栏/色板重绑当前页 `InkController`。
- PPT session 接入 `ShellController` UI dispatcher：按稳定 SlideID 缓存墨迹，切页时捕获/清空/恢复；显示开始/结束状态；底部/侧边面板与页码按钮依照设置独立显示；页码点击进鼠标模式并调用 COM SlideNavigation；隐藏页、计时与上次位置由确认对话框处理；截图回调同步采屏后异步写 PNG。
- `InkAlpha` 保存于每条 `StrokeAttributes` 并随 `.icink` 往返；旧文件缺字段时使用 96 默认值。

## 历史移植对照


| 原实现 | 新位置 | 说明 |
|---|---|---|
| `MW_TouchEvents.cs` → `MainWindow_TouchDown` / `Main_Grid_TouchDown` 的触摸分档判定 | `Core/Input/TouchContactClassifier.cs` | 逐条对应：`TouchMultiplier==0 && IsSpecialScreen` 禁用橡皮；`boundsWidth > BoundsWidth` 进入橡皮候选；`> BoundsWidth * 阈值` 走面积擦并按系数缩放；其间走线条擦；否则书写 |
| `MW_TouchEvents.cs` → `GetTouchBoundWidth` | `TouchContactClassifier.MeasureContactWidth` | 四边红外（`IsQuadIR`）取 `sqrt(width*height)`，否则取 `width` |
| `MW_Settings.cs` → `BoundsWidth` 赋值 | `TouchContactClassifier.BoundsWidth` | `IsEnableNibMode ? NibModeBoundsWidth : FingerModeBoundsWidth` |
| `MW_MatrixTransform.cs` → `GetMatrixTransformCenterPoint` | `Core/Ink/MatrixTransformCenter.cs` | 三种枢轴选项；未 Loaded 时一律画布中心 |
| `Helpers/LogHelper.cs` | `Core/Diagnostics/FileLog.cs` | 同文件名 `Log.txt`、同行格式 `{ISO} [{Type}] {msg}`、同「失败即忽略」行为；日志目录改为 %APPDATA% 以适配只读安装目录 |
| `MW_AutoTheme.cs` → `GetMainWindowTheme` / `SetBoardTheme` | `Core/Theming/ThemeResolver.cs` | 非屏幕画笔模式跟随黑板/白板选择，否则跟随应用主题。WPF 的 ResourceDictionary 切换在 WinUI 3 由 `RequestedTheme` 承担，故只移植判定逻辑 |
| `Main_Grid_TouchDown` 的 `GestureOnly` 手指翻页 | `InkController.SlideFlipRequested` + `ShellController` 接线 | 触发条件 1:1（放映中 + 无墨迹 + 允许手指控制）；位移阈值 60px 为本移植自定，原实现由 `iniP` 比较得出 |

### 刻意未移植的一处

原 `Main_Grid_TouchDown` 中的 `inkCanvas.Opacity = 1`（及配套 `0.1` 压暗）**没有搬过来**。
那正是笔锋闪烁的第二个来源，本次迁移已修复；1:1 照搬会把 bug 带回来。
代码注释中已就地标注。

## 先前轮次（已验证）

| 原实现 | 新位置 | 状态 |
|---|---|---|
| `MW_SimulatePressure&InkToShape.cs` 笔锋 | `Core/Ink/TaperProfile.cs` | **重写并验证**：改为增量因果计算，单测 + 运行时自检钉住「提交几何 == 最后一帧」 |
| `MW_SelectionGestures.cs` 选区手势 | `Core/Selection/SelectionGeometry.cs` | 已验证（13 项单测 + 实机缩放/撤销） |
| `MW_TimeMachine.cs` / `Helpers/TimeMachine.cs` | `Core/Replay/*` + `Views/ReplayWindow.cs` | 已验证（13 项单测 + 实机回放）。此行映射的是保留的本体「墨迹回放」；课堂录制 / 会话回放部分已拆为独立插件「时光机」，不在此迁移表内 |
| `Helpers/AutoUpdateHelper.cs` | `Core/Update/*` + `Features/Update/UpdateService.cs` | 已验证（26 项单测；下载长度校验；安装前确认） |
| `Helpers/EdgeGestureUtil.cs` | `Platform/System/EdgeGestureService.cs` | 已验证属性写入成功；补上原实现缺失的 `Commit()` |
| `Helpers/WinTabWindowsChecker.cs` | `Platform/System/WindowEnumerator.cs` | 以 `EnumWindows` 替代 WPF-only 的 UI Automation |
| `MW_ShapeDrawing.cs` | `Core/Shapes/ShapeGenerator.cs` | 已验证（26 种图形均产出几何） |
| `Helpers/InkRecognizeHelper.cs`（InkAnalyzer） | `Core/Recognition/ShapeRecognizer.cs` | **重写**：托管几何拟合，判定边界与 InkAnalyzer 不一致 |
| `Resources/Settings.cs` | `Core/Settings/AppSettings.cs` | 已验证 JSON 键名兼容旧文件 |

## 仍未移植

| 项 | 原因 |
|---|---|
| `MW_ElementsControls.cs` / `Helpers/InkCanvasElementsHelper.cs`（图片等元素插入与变换） | 需要独立的元素层与命中模型，尚未实现 |
| `MW_Notification.cs` / `Windows/YesOrNoNotificationWindow.xaml` | 通知条 UI 未移植；当前用 `ContentDialog` 承担确认 |
| `Windows/CycleProcessBar.xaml`、`Helpers/SoftwareLauncher.cs`、`Helpers/WindowMemoryHelper.cs` | 辅助 UI / 启动器 / 窗口位置记忆，未移植 |
| 旧 `.isf` 墨迹存档读取 | 依赖 `System.Windows.Ink`，WinUI 3 无此栈；已改用 `.icink` |
