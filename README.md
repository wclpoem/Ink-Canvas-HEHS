<div align="center">

<img src="./Images/Ink Canvas Artistry.png" style="width:96px;"/>

# Ink Canvas · WinUI 3

[![LICENSE](https://img.shields.io/badge/License-GPL--3.0-red.svg)](./LICENSE)

基于 [InkCanvas/Ink-Canvas-Artistry](https://github.com/InkCanvas/Ink-Canvas-Artistry)
（其上游为 [WXRIW/Ink-Canvas](https://github.com/WXRIW/Ink-Canvas)）的 **WinUI 3 迁移版**。

</div>

## 这个分支做了什么

把原来的 WPF / .NET Framework 4.7.2 应用整体迁移到 **WinUI 3（Windows App SDK）**，
界面按 Fluent 重做并完整适配触摸与动画，同时修掉了**笔锋闪烁**。

- **目标框架**：`net10.0-windows10.0.26100.0`，x64，**非打包**部署（无需 MSIX、无需 VS UWP 工作负载）。
- **墨迹引擎重写**：WinUI 3 没有 `InkCanvas` / `System.Windows.Ink`，改用 Win2D 自绘可变宽度笔画。
- **图形识别自研**：以托管几何拟合替换 Tablet PC `InkAnalyzer`，因此不再被锁在 x86。
- **PPT / WPS**：COM `dynamic` 后期绑定，不再依赖 Office PIA。
- 构建与最小环境说明见 **[BUILDING.md](./BUILDING.md)**；架构与取舍见
  **[设计文档](./docs/superpowers/specs/2026-11-13-winui3-migration-design.md)**。

## 笔锋闪烁是怎么修的

原实现在 `StrokeCollected`（**抬笔之后**）才计算笔锋，并整体替换 `StylusPoints`：
`InkStyle=0` 把末尾 10 点的压感从 0.5 斜坡降到 0.1，`InkStyle=1` 按速度重算**每一个点**。
于是抬笔瞬间笔尾（或整笔）宽度突变。另外手掌误触判定会把整个墨迹层 `Opacity` 压到 0.1 再拉回 1，
造成第二处明暗跳变。

本分支的做法：

1. **宽度逐点增量计算并落库**。`InkPoint.Width` 是存储值，渲染是存储数据的纯函数。
2. **笔锋只跟移动的笔尖走**。末尾 N 点的滚动窗口内收窄，笔尖移过即冻结为全宽；
   某点宽度随时间单调不减，所以视觉变化只发生在笔尖处，读起来是笔锋而不是跳变。
3. **速度笔锋改为因果**：只用已到达的点做后向差分，绝不引用未来点。
4. **抬笔不重算**，提交是「完成 + 入文档 + 记历史」的原子操作，只触发一次重绘。
5. **手掌判定不再改墨迹层透明度**，只影响输入路由。

判据被测试钉住：`TaperTests.LastLiveFrameEqualsCommittedGeometry`，
以及运行时 `--selftest` 里的 `widthsIdentical` / `outlineIdentical`。

## 模块划分

插件 SDK、内置模块生命周期、独立进程宿主和分包方式见 [模块与插件](docs/EXTENSIONS.md)。屏幕内容跟随已拆成可选模块，外部插件通过“设置 → 模块与插件”管理。

PDF 阅读与批注作为原生 WinUI 3 可选模块分发，支持原主工具栏批注、矢量墨迹保存、再次编辑、直接打开 PDF 和导入画板。安装、触摸交互及验证方法见 [PDF 模块](docs/PDF.md)。

解析几何模块支持函数、隐式方程、参数方程和极坐标，提供原生公式键盘、Windows 本地手写候选及画布实时预览。插入的图形保留公式，可整体选择、变换、克隆及再次编辑。用法见 [解析几何](docs/ANALYTIC-GEOMETRY.md)。

「时光机」是可选的课堂录制模块（Id `inkcanvas.time-machine`）：录制画板板书、白板文件与 PPT 放映批注的笔画级事件和章节书签，并导出 MP4 视频；可在「设置 → 模块与插件」中禁用或卸载，未安装时本体功能不受影响。课堂录制保存为单个 `.icink` 文件（ZIP，`inkcanvas-classroom` v1）；未启用插件打开此类文件会提示需要安装并启用「时光机」插件。用法见 [时光机插件操作说明](docs/timemachine-user-guide.md)。

屏幕批注的滚动、缩放和页面记忆可在“设置 → 实验性设置”启用，默认关闭。适配 Word、PowerPoint 与 Edge，识别范围及验证方法见 [屏幕内容跟随说明](docs/SCREEN-TRACKING.md)。

```
src/InkCanvas.Core        纯域模型（无 UI 依赖）：墨迹、笔锋曲线、几何、撤销、设置、图形生成与识别
src/InkCanvas.Platform    Win32：分层透明覆盖层窗口、笔/触摸输入、手掌判定、热键、置顶、自启
src/InkCanvas.Ink         Win2D 渲染 + InkController（对外唯一墨迹入口）+ 双指手势
src/InkCanvas.Controls    WinUI 3 控件、主题 token、动画、触摸尺寸
src/InkCanvas.Features    画板分页 / PPT / 倒计时 / 抽奖 / 截图 / 回放 / 持久化
src/InkCanvas.App         应用头：窗口外壳、模式切换、依赖装配
tests/InkCanvas.Core.Tests  单元测试（含笔锋回归）
```

依赖单向：`App → Features → Controls → Ink → Platform → Core`。

## 为什么覆盖层不是 WinUI 3 窗口

**WinUI 3 的 XAML 窗口无法逐像素透明。** 实测五种手法（空 backdrop + 透明根、
`WS_EX_NOREDIRECTIONBITMAP` 在 XAML 初始化前/后、DWM blur-behind、两种 accent 政策）
输出与不透明对照组**逐像素一致**。

因此屏幕画笔的墨迹面用 **Win32 分层窗口 + Win2D 预乘 alpha**（实测背景保留率 97.65%、
半透明笔画正常），而**其余所有界面（工具栏、画板、设置、对话框）都是 WinUI 3 窗口**。

## 快速开始

```powershell
.\build.ps1 build
.\build.ps1 test
.\build.ps1 run
```

## 已知兼容性中断

选择、旋转、克隆与套索擦的操作说明和验证范围见 [SELECTION.md](./docs/SELECTION.md)。选择模式下从空白处拖动即可框选；顶部圆形控制柄负责旋转，Shift 按 15° 吸附；开启「克隆」后可从选区连续拖出副本；「套索擦」可圈选后整项删除对象，并一次撤销恢复。

- **旧 `.isf` 墨迹存档无法读取**。ISF 依赖 `System.Windows.Ink`，WinUI 3 无此栈。
  新格式为 `.icink`（白板存档为 JSON / ZIP 笔画数据；「时光机」插件的课堂录制文件复用 `.icink` 扩展名，ZIP 容器内 `classroom.json` 标记 `inkcanvas-classroom` v1，需插件读取）。
- 设置文件 `Settings.json` **保持原 JSON 键名**，可直接沿用。
- 图形识别为自研拟合，判定边界与 `InkAnalyzer` 不完全一致。
- 上游 502 KB 单体设置 XAML 未逐像素复刻，改为按真实设置模型重建的分组设置页。

## 尚未完成 / 验证边界（如实记录）

主要功能缺口已在 3.1.0–3.3.0 持续补齐。3.4.0 修复窗口拖动与工具栏折叠状态，并补充白板双指手势、嵌入式视频与原版工具入口。鼠标模式用独立指针图标；颜色/图形菜单用独立窗口避免工具栏宿主裁切。

最新白板改动已通过 Release 编译、194 项单元测试及相关运行时回归：换色按钮状态、手势面板、当前画布内回放及浮动控制栏、书写缓存。240 条历史墨迹上的离屏帧耗时中位数从 29.55 ms 降到 0.63 ms；详见 [性能说明](./docs/PERFORMANCE.md) 与 [白板对照](./docs/board-porting-notes.md)。完整自检仍记录原有工具栏透明回退失败；真实触控、压感笔与屏幕帧率仍需目标设备验证。

| 项 | 现状 |
|---|---|
| 特殊屏 / QuadIR / `touchMultiplier` / 笔尖-手指分档 | **已 1:1 移植，但未验证**。语义取自原 `MW_TouchEvents.cs`（四边红外取 `sqrt(w·h)`；特殊屏倍数为 0 时禁用橡皮），阈值手感需在真实红外屏上复核 |
| 手势枢轴选项、文件日志、主题判定、手指翻页 | **已 1:1 移植，但未验证** |
| 真实压感笔与触摸屏 | **未在硬件上验证**。开发机无数位板；已验证部分也是走真实输入管线的合成样本 |
| WPF 白板功能/界面 | 已重建分页、三栏底栏、工具、图片/视频对象、保存/打开；不是逐像素复制，旧 ISF 媒体兼容仍缺 |
| PowerPoint 适配 | COM 服务、按页墨迹、导航与四个页码/箭头面板已接线；隐藏页/计时提示有确认对话框；**本轮未验证 WPS/Office 实机 COM 行为** |
| 图片等额外元素/通知条/启动器/窗口位置记忆 | 媒体对象、原通知条、启动器、窗口位置记忆未移植，见各 porting notes |
| 旧 `.isf` 存档 | 读不了（见上） |
| 设置页外观 | 按原字段重建响应式分组，非上游逐像素复刻 |

覆盖层性能：已提交层缓存 + 只回读活动笔画包围盒。实测**连续书写时每帧回读约 205 KB，
相当于整屏 8.29 MB 的 2.5%（约 40× 降低）**；整轮自检（含多次提交，每次提交需重建缓存）
平均为整屏逐帧方案的 10.4%。数据见 `docs/superpowers/evidence/runtime-selftest.json`。

## 许可与致谢

GPL-3.0，见 [LICENSE](./LICENSE)。原项目作者与贡献者保留全部署名；
`MW_SimulatePressure&InkToShape.cs` 中 WXRIW 的版权声明所述算法在本分支被重写，
其笔锋意图（末尾收细、速度影响粗细）被保留并以因果方式重新实现。
