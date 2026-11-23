# 模块与插件

项目将书写、选择、擦除、画布和文件模型保留在主体依赖层。可选功能通过模块接口注册，外部插件通过独立进程与版本化 SDK 接入。

## 项目边界

| 项目 | 职责 | 依赖边界 |
| --- | --- | --- |
| InkCanvas.Core | 墨迹、几何、历史与设置模型 | .NET 基础库 |
| InkCanvas.Platform / InkCanvas.Ink | 系统输入、窗口与墨迹渲染 | Core 与 Windows 平台 |
| InkCanvas.Features | 画布对象、文件存储和现有功能服务 | 既有领域及平台层 |
| InkCanvas.Extensibility | API v1、模块生命周期、插件命令与 IPC 数据 | .NET 基础库 |
| InkCanvas.Host.Contracts | 内置模块可使用的明确宿主接口 | 既有领域及渲染层、SDK |
| InkCanvas.Extensions | 模块依赖图、插件发现、故障状态和进程管理 | SDK |
| InkCanvas.Modules.ScreenTracking | 首个独立分包的内置模块，包含页面记忆、Office/UIA 观察和跟随协调 | Host.Contracts、SDK，UIA 为包内私有依赖 |
| InkCanvas.Pdf / InkCanvas.Modules.Pdf | PDF 文件与矢量墨迹持久化、WinUI 3 文档工作区 | Host.Contracts 与共用墨迹控件，PDF 解析依赖随模块分发 |
| InkCanvas.AnalyticGeometry / InkCanvas.Modules.AnalyticGeometry | 有界公式解析、曲线采样与原生公式编辑器 | Host.Contracts、共用墨迹控件与 Windows 本地手写服务 |
| InkCanvas.Modules.AiAssistant | AI 会话、SSE 网络传输、识别与纠错、原生 WinUI 3 助教窗口 | Host.Contracts 与共用控件，通过明确宿主接口对接画布和课堂 |
| InkCanvas.Modules.Library | 文稿库、目录导航、保存选择和按需页面预览 | Host.Contracts、共用控件与白板存储，独立状态和预览缓存 |
| InkCanvas.Modules.TimeMachine | 课堂录制：画板 / 白板 / PPT 批注的笔画级事件与章节书签记录、MP4 视频导出 | Host.Contracts 与共用控件，通过明确宿主接口对接画板、PPT 与课堂信号；未安装时本体功能不受影响 |
| InkCanvas.ShellExtension | 文件资源管理器缩略图与首页预览 | 原生 COM、WIC 和 Win32，独立安装，使用系统隔离宿主 |
| InkCanvas.PluginHost | 一个外部插件对应一个工作进程 | SDK、扩展运行时 |
| InkCanvas.App | 主体组合根、窗口和交互入口 | 宿主契约与运行时，通过服务接口调用模块 |

App 对屏幕跟随项目使用仅构建引用。其实现 DLL 和 UIA DLL 存放在模块目录，不进入主体运行依赖清单。移除该模块后，主体仍能启动、书写、撤销及管理插件。

PDF 模块采用同样的独立分包方式。`IncludePdfModule=false` 可发布基础包，安装包和原生文档工作区接口见 [PDF 模块](PDF.md)。

AI 助教通过独立原生插件安装，使用 `IncludeAiAssistantModule=false` 可发布不含 AI 的基础包。安装、配置与构建方法见 [AI 助教插件](AI-ASSISTANT.md)。

文稿库通过 `IBoardLibraryService` 对接打开、保存入口，使用 `IncludeLibraryModule=false` 可发布不含文稿库的基础包。资源管理器插件单独注册到当前用户。安装和预览格式见 [文稿库与资源管理器预览](BOARD-LIBRARY.md)。

「时光机」模块（`inkcanvas.time-machine`）通过 `ITimeMachineService` 暴露录制启停、录制列表与文件打开能力；课堂录制保存为插件私有目录下的 `.icink` 文件。模块停用或卸载后，本体不再出现录制命令；打开课堂录制文件时由宿主格式守卫提示需要安装并启用该模块。操作说明见 [时光机插件](timemachine-user-guide.md)。

现有课堂工具和 PPT 服务仍在原项目中。本轮用屏幕跟随完成实际拆包，并为后续迁移提供同一套注册、契约和打包入口。

## 包目录

```text
InkCanvas.exe
InkCanvas.Extensibility.dll
InkCanvas.Extensions.dll
InkCanvas.Host.Contracts.dll
Modules/
  inkcanvas.screen-tracking/
    module.json
    InkCanvas.Modules.ScreenTracking.dll
    InkCanvas.Modules.ScreenTracking.deps.json
    Interop.UIAutomationClient.dll
    UIAutomation-Interop.txt
PluginHost/
  InkCanvas.PluginHost.exe
  InkCanvas.PluginHost.dll
  InkCanvas.PluginHost.runtimeconfig.json
  ...
Plugins/
  sample.guide-lines/
    plugin.json
    InkCanvas.SamplePlugin.dll
    InkCanvas.SamplePlugin.deps.json
```

插件也可放在用户配置目录的 `Plugins` 中。“设置 → 模块与插件”提供目录入口、状态、失败原因、启停开关和命令。新增包在重启时发现，外部插件默认停用。启停外部插件即时生效，内置模块开关下次启动生效。

模块与插件的偏好保存在独立的 `Extensions.json`。插件数据按 ID 分别保存在配置目录下的 `PluginData/plugin-<id>`，主体 `Settings.json` 与 `.icink` 格式保持兼容。

## 在线下载安装与更新

在“设置 → 模块与插件 → 仓库插件”查看项目 GitHub Release 中的插件，点击“安装”或“更新”。“一键更新全部”会逐个处理已有插件的新版本，单个包失败后继续处理其余插件。下载期间可查看来源、进度、速度并取消。插件需要较新的宿主时，安装按钮禁用并显示所需主程序版本。

仓库列表、插件下载与主程序更新共用 `UpdateService`、`UpdateTransport` 和 `UpdateCache`。优先访问 GitHub 官方源，再按现有镜像顺序重试。API 不可用时读取公开 Release 页面。下载核对文件长度与 SHA256，校验成功的包可复用本地缓存。已获取的仓库列表保存在用户配置目录，离线时仍可查看。

原生模块和外部命令插件解包到用户配置目录的 `ExtensionUpdates/staged`，验证清单 ID、版本、API、入口文件与解包路径后加入待安装列表。点击“重启并应用”可一次应用全部已准备的插件。重启复用更新辅助进程，等原进程完全退出后启动新版实例，释放原生 DLL 占用。启动时在模块加载前替换用户目录中的包，并保留前一份包供中断恢复使用。下载、解包或校验失败时继续使用原包。

在线安装的原生模块位于用户配置目录的 `Modules/<id>`，外部命令插件位于 `Plugins/<id>`。宿主选用用户包与程序随附包中较新的版本。插件开关及 `PluginData` 保持各自原目录，首次安装的外部命令插件在重启后可手动启用。正在运行的原生模块通过重启切换版本。

资源管理器预览扩展使用同一下载与校验流程，随后执行包内的 `Install.ps1` 为当前 Windows 用户安装。安装结果显示在设置中。文件被资源管理器占用或系统执行策略阻止脚本时，会显示安装失败的具体原因，可处理后重试。

## 生命周期和故障处理

内置模块实现 `IApplicationModule`。`ModuleRuntime` 根据清单依赖先启动提供者，再启动消费者，退出时按逆序停止。缺失依赖、循环依赖、API 不兼容或启动异常会进入失败状态。模块拥有的清理动作逆序执行，导出服务随停止撤回，依赖模块一起停止。

模块通过 `IModuleContext.GetService<T>()` 获取已明确注册的宿主接口，通过 `Export<T>()` 提供能力。宿主使用 `ModuleRuntime.Invoke<T>()` 调用，模块事件和定时器使用 `Protect()` 包装。新增资源应立即通过 `RegisterCleanup()` 登记，启动中途失败也能清理。

外部插件实现 `IInkCanvasPlugin`，启动时登记命令。每个插件使用独立工作进程和程序集加载上下文，私有依赖通过 `AssemblyDependencyResolver` 解析，SDK 类型由工作进程共享。插件的 DLL、原生依赖和 WinUI 资源不进入主程序加载上下文。

插件请求使用当前用户可访问的命名管道，单条消息上限 1 MiB。初始化和命令有超时，异常、进程退出、超时及非法返回数据只撤回该插件的命令并终止其工作进程。管理器销毁进程时释放状态锁，避免退出回调锁竞争。主体退出后，工作进程监测父进程结束并退出。

独立进程提供运行故障和依赖隔离。插件进程仍具有当前用户的系统权限，当前实现没有权限沙箱。内置模块属于受信任的应用组成部分，通过宿主登记的已知包加载。

## 编写插件

插件项目引用 `InkCanvas.Extensibility`，目标 `net10.0`，启用 `EnableDynamicLoading`，将 SDK 引用设为 `Private="false"`。完整的可构建示例位于 `samples/InkCanvas.SamplePlugin`。

```json
{
  "Id": "sample.guide-lines",
  "Name": "参考线示例插件",
  "Version": "1.0.0",
  "ApiVersion": 1,
  "EntryAssembly": "InkCanvas.SamplePlugin.dll",
  "EntryType": "InkCanvas.SamplePlugin.GuideLinesPlugin",
  "Capabilities": ["CanvasWrite"]
}
```

API v1 提供命令、画布视口元数据、通知和声明式笔画输出。插件不能取得主程序的 `AppServices`、控制器或窗口引用。宿主完整验证结果后，将输出转换为可编辑墨迹，并记录为一个撤销单元。执行期间用户切页、关闭画布或开始书写时，过期结果会被丢弃。

每条结果最多包含 256 条笔画，总计 16384 个点，坐标及宽度须有限，笔宽范围为 0.5–120 DIP。插件须声明 `CanvasWrite` 或 `Notifications` 能力，结果须与声明一致。命令 ID 在插件内唯一，宿主通过插件 ID 与命令 ID 共同定位。

后续增加图片、媒体、设置页或自定义视图时，应扩展明确的 DTO 与宿主验证入口，并维护 API 版本兼容关系。

## 构建与分包

```powershell
$env:NUGET_PACKAGES = 'D:\dev-cache\nuget'
dotnet publish src/InkCanvas.App/InkCanvas.App.csproj -c Release -p:Platform=x64 -o artifacts/app-modular
dotnet publish src/InkCanvas.App/InkCanvas.App.csproj -c Release -p:Platform=x64 -p:IncludeScreenTrackingModule=false -o artifacts/app-modular-core
dotnet pack src/InkCanvas.Extensibility/InkCanvas.Extensibility.csproj -c Release -p:Platform=x64 -o artifacts/extension-packages
.\tools\pack-extension.ps1 -Project samples\InkCanvas.SamplePlugin\InkCanvas.SamplePlugin.csproj -Kind Plugin
.\tools\pack-extension.ps1 -Project src\InkCanvas.Modules.ScreenTracking\InkCanvas.Modules.ScreenTracking.csproj -Kind Module
```

发布时使用新的输出目录，以免旧包残留。脚本在 `artifacts/extension-packages` 生成 ZIP，并保留独立 staging 目录供检查。插件 ZIP 解压到 `Plugins`，模块 ZIP 解压到 `Modules`。清单中的 `PackageFiles` 列出模块自己的额外 DLL、资源与许可证，主体共享依赖由宿主提供。

手动复制或更新包前停用对应插件，内置模块在程序关闭后替换。插件更新保留同一 ID 即可继续使用原来的独立数据目录。

## 验证

`ExtensionRuntimeTests` 覆盖依赖排序与逆序停止、启动回滚、事件故障、缺包、禁用、循环、清单兼容、IPC 长度、结果校验、插件重启、多进程及数据隔离、退出与超时，以及启动期间改变启用意图。

`ExtensionInstallTests` 覆盖仓库版本选择、公开发布页回退、共用下载缓存、取消、路径验证、清单不匹配、暂存损坏、重复包、待安装列表恢复和新版宿主随附包优先。`--extensionupdatescheck=<目录>` 使用真实 Release 插件包验收设置按钮、下载校验、重启安装和工作进程启动，并检查批量更新中的单包失败处理。验收使用独立的用户数据目录，资源管理器扩展的 Windows 注册流程需要单独实机验证。

`--extensionscheck=<目录> --extensionpackage=<示例插件包目录>` 通过真实设置按钮调用外部插件，验证工具栏入口、画布输出、撤销重做和停用。基础包使用 `--expect-no-screen-module` 验证缺少模块仍可书写。既有 `--screenfollowcheck`、`--workspacecheck`、`--settingscheck` 等回归入口继续可用。
