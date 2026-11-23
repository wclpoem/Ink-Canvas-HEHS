# 安装与文件打开方式

GitHub Release 同时提供 MSI 安装版和 ZIP 便携版。两者均包含 Windows App SDK，首次运行需要 .NET 10 x64 运行时。MSI 会检查运行时，缺少时显示下载地址。

## MSI 安装版

双击 `InkCanvas-WinUI3-版本-win-x64.msi`，默认安装到当前用户的 `%LOCALAPPDATA%\Programs\Ink Canvas`。安装向导可选择目录，开始菜单提供 Ink Canvas 入口。安装和升级由 Windows Installer 管理，在 Windows 设置的“已安装的应用”中可卸载。

安装后，右键 PDF，选择“打开方式 → Ink Canvas”即可在原生 PDF 窗口查看与批注。白板 `.icink` 文件同样注册。Windows 的默认应用选择由用户在“打开方式”或系统设置中确认。注册方式遵循 Windows 的应用能力和 OpenWithProgids 接口。[应用注册文档](https://learn.microsoft.com/en-us/windows/win32/shell/app-registration)

学校部署可使用 `msiexec /i "InkCanvas-WinUI3-版本-win-x64.msi" /qn /norestart`。该安装包针对当前登录用户，不需要管理员提权，批量部署应以目标教师的用户身份执行。`INSTALLFOLDER="路径"` 可指定安装目录。

安装版自动更新选择 Release 中的 MSI，通过与插件共用的并发选源和 SHA256 校验后，保存画布并退出，由辅助进程调用 Windows Installer 执行升级。升级日志位于更新缓存内的 `msi-install.log`。用户配置和白板保存在现有用户数据与文稿路径。

## ZIP 便携版

解压到可写目录后运行 `InkCanvas.exe`。右键 PDF，在“打开方式”中选择“在电脑上选择应用”，指向该程序，也可直接把 PDF 拖到程序图标。

需要将便携版显示在系统推荐的应用列表时，可运行 `InkCanvas.exe --register-file-associations`。此操作为当前用户登记 PDF 和白板类型。便携版移动目录后，应从新路径重新登记。

便携版继续选择 ZIP 更新包，校验后通过原有文件替换与回滚流程更新。

## 构建 MSI

安装固定工具版本 `dotnet tool install wix --tool-path "$env:LOCALAPPDATA\InkCanvas\BuildTools\wix" --version 5.0.2`，再运行该目录的 `wix.exe extension add -g WixToolset.UI.wixext/5.0.2` 和 `wix.exe extension add -g WixToolset.Netfx.wixext/5.0.2`。

`tools/package-winui-release.ps1` 会同时制作 ZIP、MSI 和插件包，SHA256 清单覆盖所有发布附件。也可调用 `tools/build-msi.ps1 -PayloadDirectory "发布目录" -Version "版本" -OutputDirectory "输出目录"`。MSI 使用稳定的 UpgradeCode 和文件组件标识，以支持升级、修复与卸载。
