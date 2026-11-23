# PPT/WPS 课件穿透批注与翻页联动（P0）

本文件记录 P0 阶段（原需求第 2、3 节：PPT/WPS 课件穿透批注与翻页联动）的实际交付内容与验证边界。所有结论以当前工作区代码和本机实际执行的命令为准；未执行、未复现的项目一律标注「未验证」。

## 1. 范围与阶段

| 阶段 | 内容 | 状态 |
|---|---|---|
| P0 | 放映窗口跟踪、覆盖层按放映窗口对齐、四角翻页器（悬停增强 / 批注切换 / 位置循环 / 收起）、按页批注的课件路径指纹与页序检测、翻页防重入与按键降级、独立 P0 单测 | 已交付（Office 2021 真实放映联调通过；WPS 联调待补，见第 6、7 节） |
| P1 | 多页画板、课堂工具箱 | 本次不实现，仅登记 |
| P2 | 课堂录制（原板书时光机） | 已拆为独立受信模块「时光机」（Id `inkcanvas.time-machine`），见 [时光机插件操作说明](timemachine-user-guide.md)；本 P0 课件联动文档不展开 |

平台：WinUI 3（C#，.NET 10），x64。P0 不涉及 HarmonyOS 侧代码，`harmony/`、`parity/` 未改动。

## 2. 模块结构

| 文件 | 职责 | 关键 API |
|---|---|---|
| `src/InkCanvas.Features/Ppt/PresentationIdentity.cs` | 课件身份：规范化路径 + 文件指纹（SHA-256 前 16 字节），用于按课件隔离按页墨迹 | `BuildKey(PptPresentationInfo)`、`NormalizePath(string)`、`Hash(string)` |
| `src/InkCanvas.Features/Ppt/SlideMappingInspector.cs` | 页序、缺页、页数变化检测，决定恢复批注时是否可信 | `Inspect(PptInkArchive, IReadOnlyList<PptSlideInfo>)` |
| `src/InkCanvas.Features/Ppt/NavigationGuard.cs` | 翻页防重入（同一时间仅允许一次导航）与外部翻页回环抑制 | `TryEnter(long)`、`Exit(long)`、`SuppressExternal(long)`、`IsSuppressed(long)` |
| `src/InkCanvas.Features/Ppt/SlideSwitchGate.cs` | 以 session/generation 丢弃过期的异步切页回调 | `BeginSession()`、`NextGeneration()`、`IsCurrent(int, int)` |
| `src/InkCanvas.Features/Ppt/NavigationLayout.cs` | 翻页器四角定位、屏幕边界钳制与四角循环 | `Place(...)`、`NextCorner(NavigationCorner)` |
| `src/InkCanvas.Features/Ppt/PptInkSession.cs`（既有，P0 接线复用） | COM 状态轮询、按页墨迹缓存、截图与存档 | `Poll()`、`Next()`、`Previous()`、`GoToSlide(int)`、`ExitSlideShow()`、`Save()` |
| `src/InkCanvas.Features/Ppt/PptInteropService.cs`（既有，P0 接线复用） | PowerPoint/WPS 后期绑定 COM 调用与断开 | `TryAttach()`、`Next()`、`Previous()`、`Detach()` |
| `src/InkCanvas.Platform/Windows/ShowWindowTracker.cs` | 放映窗口矩形、所在显示器、DPI、全屏判定（GetWindowRect / Monitor / DPI） | `TryGet(nint, out ShowWindowInfo)` |
| `src/InkCanvas.Platform/Windows/PresentationFallbackDetector.cs` | COM 不可用时按全屏窗口 + 进程名/窗口类名降级识别放映窗口 | `Detect()` |
| `src/InkCanvas.Platform/Input/KeyboardInjector.cs` | SendInput 注入 PageDown / PageUp，作为 COM 翻页失败时的降级路径 | `PageDown()`、`PageUp()`、`PressVirtualKey(ushort)` |
| `src/InkCanvas.Platform/Windows/LayeredInkWindow.cs`（修改） | DPI 缩放可更新；`SetBounds` 时重建 DIB，避免覆盖层尺寸变化后拉伸失真 | `DpiScale`、`SetBounds(...)` |
| `src/InkCanvas.App/Services/OverlayHost.cs`（修改） | 覆盖层对齐到放映窗口 / 复位到主屏 | `AlignTo(ShowWindowInfo)`、`ResetToPrimary()` |
| `src/InkCanvas.App/Views/PptNavigationWindow.cs`（修改） | 单窗承载四角翻页器；0.35 悬停增强；批注、位置、收起按钮 | `PptNavigationWindow(PptNavigationPlacement, IShellActions, int)`、`Placement` |
| `src/InkCanvas.App/Services/ShellController.cs`（修改） | 250/1200 ms 自适应轮询、降级放映识别、导航单实例、防重入、按键降级、切页清选区、gate 接线 | `RotatePptNavigationCorner()`、`CollapsePptNavigation()`、内部 `UpdatePptNavigation(PptSnapshot)` |
| `src/InkCanvas.Controls/Controls/FloatingBar.cs`（修改） | `IShellActions` 新增导航栏操作入口 | `RotatePptNavigationCorner()`、`CollapsePptNavigation()` |
| `tests/InkCanvas.P0.Tests/` | 独立 P0 纯逻辑测试项目（链接 `tests/InkCanvas.Core.Tests/PptP0Tests.cs`，不改动既有测试工程） | `dotnet test`，22 项 |

## 3. 数据流

```
PowerPoint / WPS 放映中
        │  COM 轮询（放映中 250 ms；非放映 1200 ms）
        ▼
PptInteropService.Poll() ──► PptSnapshot（连接态、放映态、当前页、页数、演示文稿信息）
        │                             │
        │                             ├─► PresentationIdentity.BuildKey()  课件路径 + 文件指纹 → 按课件隔离
        │                             └─► SlideMappingInspector.Inspect()  页序/缺页/页数变化 → 恢复批注可信度
        ▼
PptInkSession：按页取/存墨迹（SlideID 稳定分页），切页前截图、切页后清选区
        │
        ▼
ShellController.UpdatePptNavigation(snapshot)
        ├─► ShowWindowTracker.TryGet() 成功 → OverlayHost.AlignTo(bounds)（跟随放映窗口与 DPI）
        ├─► COM 不可用 → PresentationFallbackDetector.Detect() → 同上
        ├─► 无放映窗口 → OverlayHost.ResetToPrimary()
        └─► NavigationLayout.Place() → PptNavigationWindow（单实例、四角循环、0.35 悬停增强）
                    │
                    ├─ 翻页 → NavigationGuard.TryEnter() 防重入 → PptInteropService.Next()/Previous()
                    │        └─ COM 调用失败 → KeyboardInjector.PageDown()/PageUp()
                    ├─ 位置 → RotatePptNavigationCorner() → NavigationLayout.NextCorner()
                    └─ 收起 → CollapsePptNavigation()
```

切页回调统一经 `SlideSwitchGate` 携带 session/generation 校验，过期回调直接丢弃，避免快速连翻时旧页墨迹覆盖新页。

## 4. 兼容性与降级

- 识别顺序：优先 PowerPoint COM；`PptInteropService` 的 `ProgIds` 依次尝试 `PowerPoint.Application`、`KWPP.Application`（WPS）。
- COM 不可用（未安装、未运行、权限受限、WPS 兼容层缺接口）：`PresentationFallbackDetector` 按全屏窗口 + 进程名 / 放映窗口类名降级识别放映窗口，覆盖层仍可对齐。
- 放映窗口矩形获取失败：退回主屏（`OverlayHost.ResetToPrimary()`）。
- COM 翻页失败：降级为 SendInput 注入 `PageDown` / `PageUp`。
- COM 断开：自动 `Detach()`，释放 COM 对象；轮询回到 1200 ms 空闲节奏。
- WPS 侧兼容接口的实际行为未在真实环境验证（见第 8 节）。

## 5. 配置项与依赖

- 无新增配置文件、无新增第三方依赖、无上传行为。
- 沿用既有 `Settings`（`AppSettings`）中的 PowerPoint 键：
  - `powerPointSupport`
  - `isShowBottomPPTNavigationPanel`
  - `isShowSidePPTNavigationPanel`
  - `isShowPPTNavigationBottom`
  - `isShowPPTNavigationSides`
  - `isShowCanvasAtNewSlideShow`
- 构建 SDK：.NET 10 SDK 10.0.404，官方 zip 安装到 `D:\dotnet`，未改动 C 盘。

## 6. 自测用例清单

| 用例 | 手段 | 状态 |
|---|---|---|
| 放映进入 / 退出 | 真实 PowerPoint COM 联调（Office 2021） | 实测通过：`SlideShowSettings.Run()` 进入后枚举到 `PowerPoint 导航`、`Ink Canvas Overlay`、`Ink Canvas 工具栏` 三个窗口；退出放映后导航器窗口消失（UIA 查询 nav hidden），覆盖层保留，无新的 `TrackRoot` / COM 异常 |
| 连续翻页 / 防重入 | 单测（`NavigationGuard`、`SlideSwitchGate`）+ 实测 | 单测通过；实测通过：COM `View.Next` 外部翻页后 UIA 文本由 `1 / 3` 变 `2 / 3`，轮询联动正确 |
| 导航器点击翻页 | 真实放映中鼠标点击导航器「下一页」（坐标 144,768） | 实测通过：PowerPoint 当前页码 2 → 3，翻页器调用官方接口成功 |
| 多屏 / 不同 DPI | 需实机多显示器 | 未验证 |
| 穿透 / 批注切换 | 真实放映环境实测 | 部分通过：导航器笔按钮切换到批注模式后可在覆盖层正常落笔；穿透行为未单独验证 |
| 按页批注恢复 | 既有 `PptInkSession` 路径 + 实测 | 实测通过：第 1 页黑色 L 形墨迹 → 翻到第 2 页无残影 → 返回第 1 页墨迹完整恢复 |
| 异常退出恢复 | 代码路径存在（Detach / 降级检测 / 复位主屏） | 未实测 |
| 课件路径指纹 / 页序检测 | 单测（`PresentationIdentity`、`SlideMappingInspector`） | 通过（包含在 22 项内） |

## 7. 验证结果

所有命令均在 `D:\hw\Ink-Canvas-HEHS` 下执行。

1) 应用编译（P0 改动所在工程）

```
D:\dotnet\dotnet.exe build src\InkCanvas.App\InkCanvas.App.csproj -c Debug -p:Platform=x64
```

结果：成功，exit 0，`src` 零编译错误。

复核备注（写入本文时的环境状态）：本次复核重跑该命令失败，报 `磁盘空间不足：'C:\Users\wcl\.nuget\packages\microsoft.windowsappsdk.runtime\1.8.260921001\mv1ppatj.l4x'`，当时 C 盘剩余 0 GB（NuGet 缓存解压在 C 盘）。属环境磁盘空间问题，与 P0 代码无关；上述成功结果为此前实际执行所得。P0 单测（第 3 条）在本次复核中已重新构建并通过。2026-11-19 联调前再次重建该工程（含 `PptNavigationWindow` 关闭守卫修复），成功，0 警告 0 错误。

2) 全解决方案构建

```
dotnet build InkCanvas.slnx
```

结果：失败。原因不是 P0 改动，而是既有测试 `tests/InkCanvas.Core.Tests/VectorParityTests.cs` 引用了 `src` 中不存在的 API：`BoardElementMath`、`StrokeEraser`、`InkController.ToolChanged` / `EraserMode` / `CloneMode` / `StickyShape` / `OnPointerCanceled` / `ClearSelection` / `SelectAll` / `SelectShape` / `SelectionFrame`、`InkStroke.ExportObjectFrame`、`ShapeGenerator.GenerateCuboidDepth` / `GenerateHyperbolaBranches` / `SnapParallelEnd`、`OverlayPointer` 10 参数构造。这是基线欠账，与 P0 改动无关；未修改既有测试。

结论：**全解决方案构建受既有测试基线阻塞**；P0 验证改用独立测试工程。

3) P0 独立单测

```
D:\dotnet\dotnet.exe test tests\InkCanvas.P0.Tests\InkCanvas.P0.Tests.csproj -p:Platform=x64
```

真实输出摘要：

```
已通过! - 失败:     0，通过:    22，已跳过:     0，总计:    22，持续时间: 86 ms - InkCanvas.P0.Tests.dll (net10.0)
```

覆盖：`PresentationIdentity` 哈希/路径规范化/课件键稳定性与变化、`SlideMappingInspector` 缺页/乱序/页数变化、`NavigationGuard` 防重入与回环抑制、`SlideSwitchGate` 过期 session/generation、`NavigationLayout` 四角定位与循环。

4) 真实放映联调（2026-11-19，本机 Windows 11 + Office 2021 家庭学生版，屏幕 1536x864，DPI 100%）

环境与启动：

- 本轮重建 `src\InkCanvas.App\InkCanvas.App.csproj` 成功，0 警告 0 错误；应用为 framework-dependent 构建，直接运行 exe 报「You must install or update .NET」（系统仅装 7.0.20）。最终以 `DOTNET_ROOT=D:\dotnet` 启动成功（.NET 10.0.404 SDK/运行时装在 `D:\dotnet`）。
- 用 PowerPoint COM 生成 `D:\hw\p0-test.pptx`（3 页，标题 `P0 Slide 1..3`），`SlideShowSettings.Run()` 进入放映。

窗口与定位：

- `PowerPoint 导航`：可见，Rect `20,744,288x52`（左下角，20*dpi 边距）；尺寸由 `NavigationLayout` 计算，6 个按钮使窗口实际宽 288。
- `Ink Canvas Overlay`：可见，`0,0,1536x864`，与主屏/放映窗口同尺寸。
- `Ink Canvas 工具栏`：可见。

联动与翻页：

- UIA 读取导航器文本为 `1 / 3`，与 PowerPoint 当前放映页码一致。
- COM 外部翻页（`View.Next`）后 UIA 文本变为 `2 / 3`，轮询联动正确。
- 鼠标点击导航器「下一页」（坐标 144,768）后，PowerPoint 当前页码 2 → 3，翻页器调用官方接口成功。

按页批注隔离与恢复（导航器笔按钮切到批注模式后在画布画一笔）：

- 第 1 页：黑色 L 形墨迹。
- 翻到第 2 页：无第 1 页墨迹残影。
- 返回第 1 页：L 形墨迹完整恢复。

退出路径：

- 退出放映后导航器窗口消失（UIA 查询 nav hidden），覆盖层保留，无新的 `TrackRoot` / COM 异常。
- 期间发现并修复的缺陷：窗口关闭后 `Activated` 仍回调 `TrackRoot()` 访问已释放 Content，抛 `COMException 0x800710DD`。已在 `PptNavigationWindow` 加 `_closed` 守卫、具名 `OnActivated` / `OnClosed`、try/catch；重建 App 成功（0 警告 0 错误），复测退出路径日志无该异常。

联调遗留（不声称通过）：PowerPoint 忙时轮询会记录 `PPT/Poll: RPC_E_CALL_REJECTED (0x80010001)`，已捕获不崩溃，但记为 Error（日志噪音）；WPS 未做实际放映联调（仅验证 `KWPP` ProgID 可注册）；多屏不同 DPI 未验证；悬停透明度在 `NOACTIVATE` 下的触发未验证；`OverlayInkPresenter` 尺寸变化后的帧缓冲重建未验证；导航栏收起后无「展开」入口。

## 8. 已知边界与后续

- PowerPoint（Office 2021）真实放映联调已通过进入/退出、COM 与导航器翻页、按页批注隔离与恢复；WPS 真实放映仍未联调，当前不声称 WPS 端到端通过。
- PowerPoint 忙时轮询会记录 `PPT/Poll: RPC_E_CALL_REJECTED (0x80010001)`，已捕获不崩溃，但当前按 Error 记录，属日志噪音待降级。
- 未验证多屏不同 DPI 下的覆盖层与翻页器定位。
- 未验证 WPS（`KWPP.Application`）兼容接口的具体行为与失败点（仅验证 ProgID 可注册，未启动 WPS 放映）。
- 未验证 `NOACTIVATE` 覆盖层窗口下悬停透明度（0.35 增强）是否按预期触发。
- 未验证 `OverlayInkPresenter` 在覆盖层尺寸变化后的帧缓冲重建效果。
- 导航栏「收起」后当前无展开入口，需在后续阶段补充恢复方式。
- P1 多页画板 / 课堂工具箱本次不实现，仅登记，待排期；P2 课堂录制已由独立「时光机」插件实现，不在本 P0 范围内。

## 9. 隐私

课件路径、文件指纹与按页墨迹均保存在本地（沿用既有本地存档路径），P0 未新增任何网络请求或上传行为。