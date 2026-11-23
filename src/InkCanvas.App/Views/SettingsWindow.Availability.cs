using System.Runtime.InteropServices;
using InkCanvas.Controls.Controls;
using InkCanvas.Host.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.App.Views;

public sealed partial class SettingsWindow
{
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    private static bool HasTouch => (GetSystemMetrics(94 /* SM_DIGITIZER */) & 0x80) != 0 && (GetSystemMetrics(94) & 3) != 0;
    private static bool HasComClass(string name)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(name + "\\CLSID");
            return key?.GetValue(null) is string;
        }
        catch { return false; }
    }
    private string TouchRequirement() => HasTouch ? null : "未检测到已就绪的触摸屏，请连接触摸屏并安装驱动。";
    private string PresentationRequirement() => !S.PowerPoint.PowerPointSupport ? "请先启用 PowerPoint 支持。"
        : HasComClass("PowerPoint.Application") || S.PowerPoint.IsSupportWPS && HasComClass("KWPP.Application")
            ? null : "未检测到 PowerPoint 或已启用的 WPS 演示 COM 接口，请安装对应的桌面版应用。";

    private string SaveDirectoryRequirement()
    {
        string path = S.Automation.AutoSavedStrokesLocation;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return "请先设置完整的自动保存目录。";
            if (!Directory.Exists(path)) return "自动保存目录尚不存在，请先保存一次墨迹或选择已有目录。";
            if (Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) == Path.GetPathRoot(path)?.TrimEnd(Path.DirectorySeparatorChar))
                return "请使用专门的保存文件夹，不能对磁盘根目录启用自动清理。";
            return null;
        }
        catch { return "自动保存目录无效或无法访问，请检查路径及权限。"; }
    }

    private UIElement RequireSetting(string title, UIElement element)
    {
        Func<string> requirement = title switch
        {
            "停笔吸附等待时间 (毫秒)" or "识别圆与椭圆" or "识别三角形、矩形与平行四边形"
                or "识别立方体线框" or "识别坐标轴" or "自动对齐水平、垂直与 45° 方向"
                => () => S.InkToShape.IsInkToShapeEnabled ? null : "请先启用墨迹识别。",
            "方向吸附容差 (度)" => () => !S.InkToShape.IsInkToShapeEnabled ? "请先启用墨迹识别。"
                : !S.InkToShape.AutoAlign ? "请先启用自动对齐。" : null,
            "静默更新" => () => _updater is null ? "更新服务未启动。" : !S.Startup.IsAutoUpdate ? "请先启用自动检查更新。" : null,
            "静默更新起始时间" or "静默更新终止时间" => () => _updater is null ? "更新服务未启动。"
                : !S.Startup.IsAutoUpdate || !S.Startup.IsAutoUpdateWithSilence ? "请先启用自动检查更新和静默更新。" : null,
            "检查更新" or "版本列表" => () => _updater is null ? "更新服务未启动，请从主程序打开设置。"
                : _updater.IsBusy ? "正在处理更新，请等待当前操作完成。" : null,
            "特殊屏幕触摸大小倍数" => () => !S.Advanced.IsSpecialScreen ? "请先启用特殊屏幕模式。" : null,
            "启用多点触控模式" or "启用笔尖模式" or "四边红外模式" or "特殊屏幕模式" or "允许双指旋转选中的墨迹" => TouchRequirement,
            "双指缩放" or "双指平移" or "双指旋转" or "缩放/旋转变换中心点" => () => TouchRequirement()
                ?? (S.Gesture.IsEnableMultiTouchMode && !S.Gesture.AutoSwitchTwoFingerGesture
                    ? "请先关闭多点触控书写模式，或启用进入白板时自动切换手势。" : null),
            "Microsoft PowerPoint 支持" => () => HasComClass("PowerPoint.Application") || HasComClass("KWPP.Application") ? null
                : "未检测到 PowerPoint 或 WPS 演示桌面版的 COM 接口。",
            "WPS 支持" => () => HasComClass("KWPP.Application") ? null : "未检测到 WPS 演示 COM 接口，请安装 WPS 演示桌面版。",
            "显示 PPT 下方翻页箭头按钮" or "显示 PPT 下方页码按钮" or "显示 PPT 侧边翻页箭头按钮" or "显示 PPT 侧边页码按钮"
                or "进入 PPT 放映时自动进入批注模式" or "自动幻灯片截屏" or "自动保存幻灯片墨迹" or "记忆并提示上次播放位置"
                or "提示隐藏幻灯片" or "提示是否已启用自动播放" or "鼠标模式时在 PPT 上显示墨迹" or "进入PPT 放映时自动收纳至侧边栏" => PresentationRequirement,
            "允许使用手指手势进行幻灯片翻页" => () => PresentationRequirement() ?? TouchRequirement(),
            _ => null,
        };
        return requirement is null ? element : Require(element, requirement);
    }

    private UIElement Require(UIElement element, Func<string> reason)
    {
        if (element is not Border { Child: SettingsRow row }) return element;
        string description = row.Description;
        string originalHelp = AutomationProperties.GetHelpText(row.Editor);
        void Refresh()
        {
            string missing = reason();
            SetEnabled(row.Editor, missing is null);
            row.Description = missing is null ? description
                : string.IsNullOrEmpty(description) ? missing : description + "\n" + missing;
            AutomationProperties.SetHelpText(row.Editor, missing ?? originalHelp);
            ToolTipService.SetToolTip(row.Editor, missing);
        }
        Refresh();
        _refreshControls.Add(Refresh);
        return element;
    }

    private static void SetEnabled(FrameworkElement element, bool enabled)
    {
        if (element is Control control) control.IsEnabled = enabled;
        else if (element is Panel panel)
            foreach (var child in panel.Children.OfType<FrameworkElement>()) SetEnabled(child, enabled);
        else if (element is Border { Child: FrameworkElement child }) SetEnabled(child, enabled);
    }
}
