# 文稿库与资源管理器预览

文稿库是独立的原生 WinUI 3 功能插件 `inkcanvas.library`。安装到程序的 `Modules` 目录后，工具菜单出现“文稿库”，白板打开、保存和另存为也会使用文稿库。可以在设置的原生功能插件页面禁用，重启后生效。

文稿库需要本次构建提供的新宿主接口，随附的可运行版本已包含该插件。其他原生功能插件保持独立安装和开关。

界面使用项目共享的标题栏与 Mica 背景，分为位置导航、抬升文稿画布与详情预览三栏。左侧集中最近使用、收藏和文稿文件夹，底部提供系统文件选择器。中间支持可点击的面包屑、递归名称搜索、按名称/修改时间/创建时间/大小排序，以及缩略图与列表切换。空状态居中显示，并提供清除筛选和浏览入口。保存时选择目标文件夹并填写名称，同名文稿需要确认替换。

窗口宽度低于 880 DIP 时收起右侧详情，选中文稿后可用详情按钮展开预览浮层，或双击文稿直接打开。低于 640 DIP 时左侧导航改为抽屉。网格卡片包含 16:9 封面、页数和修改时间，并提供可触控的收藏按钮。右键或触摸长按可打开文件操作菜单。视图与排序偏好保存在文稿库设置中。

打开文稿前可以查看封面，并使用预览区左右按钮查看其他页面。预览按整个页面对象范围等比缩放，包含墨迹、图片和视频封面。新保存的 `.icink` 内嵌 `preview/cover.png` 和小型 `preview/info.json`，保留全部可编辑数据和原有 ZIP 格式的兼容性。

旧文稿会按需渲染预览。较大的旧版 JSON、超过 32 MB 的页面清单或超过 64 MB 的页面预览媒体，提示打开文稿查看。预览跳过视频主体数据。磁盘预览缓存以文件路径、大小、修改时间和页码为键，上限 128 MB。仅为可见卡片加载图片，滚出视图后释放，文件改变时自动使用新缓存键。

文稿库的最近记录、收藏和缓存位于应用设置目录的 `PluginData/inkcanvas.library`。文稿仍保存在选定的普通文件夹中。原有自动保存与退出保存设置继续生效。

安装新版 AI 助手插件后，详情区出现“AI 命名与描述”。多图分析完成后会显示可编辑的名称和摘要建议，点击“应用”才写入文件，取消建议会保留原文件。确认时检测文件是否已发生变化，更新后的名称会同步到最近记录、收藏与存档历史。较早的 AI 插件需要更新后才能使用建议确认流程。保存后的自动命名可在设置中手动开启，配置方法见 [AI 助手插件](AI-ASSISTANT.md)。

## 安装资源管理器插件

资源管理器插件为独立的 x64 原生 Shell 扩展包 `inkcanvas.shell-preview-1.0.0-win-x64.zip`。解压后，在 64 位 PowerShell 中执行 `./Install.ps1`。安装只写当前用户的注册表，并复制 DLL 到 `%LOCALAPPDATA%/InkCanvas/ShellExtension`。资源管理器打开预览窗格后可显示白板首页。缩略图跟随资源管理器图标尺寸调整。

卸载可运行安装目录中的 `Uninstall.ps1`。脚本会恢复原有的缩略图和预览关联。DLL 仍被 Windows 使用时，注销后可以删除残留 DLL。

处理器实现 `IInitializeWithStream`、`IThumbnailProvider`、`IPreviewHandler` 等原生接口，使用 Windows 的隔离宿主。按 ZIP 目录直接定位未压缩的 PNG 封面，解码图像最大边为 1024 像素。单封面限制 2 MB，PNG 尺寸和 ZIP 目录大小均有限制。无须启动 Ink Canvas、加载 .NET、WinUI、全部笔画或媒体数据。

资源管理器当前预览首页。旧文稿、缺少封面或 ZIP64 文稿会使用文档图标或提示重新保存。使用本版 Ink Canvas 重新保存后可生成封面。封面损坏或显卡设备不可用时，保存引擎仍优先保存完整的可编辑文稿。

## 构建

文稿库使用 `tools/pack-extension.ps1 -Project src/InkCanvas.Modules.Library/InkCanvas.Modules.Library.csproj -Kind Module` 打包。

Shell 插件使用 `tools/build-shell-extension.ps1` 构建与打包，需要 Visual Studio C++ v143 和 Windows SDK。生成的 DLL 使用静态 C++ 运行库，安装脚本无需管理员权限。原生验证程序工程为 `src/InkCanvas.ShellExtension/ShellExtensionCheck.vcxproj`，参数依次为 DLL 和带封面的 `.icink` 样本路径。

相关 Windows 接口说明见 [缩略图接口](https://learn.microsoft.com/en-us/windows/win32/api/thumbcache/nn-thumbcache-ithumbnailprovider) 和 [预览处理器接口](https://learn.microsoft.com/en-us/windows/win32/shell/building-preview-handlers)。
