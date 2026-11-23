namespace InkCanvas.Core.Settings;

public sealed record SettingsCategory(string Name, string Glyph, string Group, string SearchTerms);

/// <summary>Settings navigation and legacy entry points share the same category names.</summary>
public static class SettingsCategoryCatalog
{
    public static IReadOnlyList<SettingsCategory> Pages { get; } = new SettingsCategory[]
    {
        new("常规", "\uF610", "使用与书写", "启动选项 开机 启动 退出 确认 自动选项 应用兼容"),
        new("书写与擦除", "\uF3DD", "使用与书写", "画板选项 画笔 笔尖 笔锋 压感 水墨 荧光笔 橡皮"),
        new("画布与识别", "\uF1E3", "使用与书写", "画板选项 墨迹识别选项 白板 图片 背景 形状识别 对齐 自动选项"),
        new("触控与手势", "\uE6E5", "使用与书写", "手势选项 高级选项 显示器 多屏 红外屏 校准 防误触"),
        new("外观与工具栏", "\uF2F6", "使用与书写", "外观选项 自动选项 主题 字体 图标 缩放 收纳"),
        new("演示与课件", "\uF6F3", "课堂与扩展", "PPT 选项 PowerPoint WPS 放映 翻页 批注"),
        new("抽奖与点名", "\uF5B3", "课堂与扩展", "名单 班级 抽选 随机 冷却"),
        new("模块与插件", "\uF4DD", "课堂与扩展", "插件 安装 下载 更新 AI PDF 解析几何 时光机"),
        new("保存与备份", "\uF68A", "应用与数据", "自动选项 默认目录 文件名 截图 墨迹 自动保存 历史文件清理"),
        new("存储与缓存", "\uF41D", "应用与数据", "配置 空间 缓存 清理"),
        new("关于与更新", "\uF4AB", "应用与数据", "关于 Ink Canvas Artistry 启动选项 高级选项 版本 更新 日志 诊断 帮助"),
    };

    private static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["启动选项"] = "常规", ["画板选项"] = "书写与擦除", ["手势选项"] = "触控与手势",
        ["显示器"] = "触控与手势", ["高级选项"] = "触控与手势", ["墨迹识别选项"] = "画布与识别",
        ["外观选项"] = "外观与工具栏", ["自动选项"] = "外观与工具栏", ["PPT 选项"] = "演示与课件",
        ["实验性设置"] = "实验功能", ["关于 Ink Canvas Artistry"] = "关于与更新",
    };

    public static string Resolve(string name) => name is not null && Aliases.TryGetValue(name, out var current) ? current : name;
    public static string SearchTerms(string name) => Pages.FirstOrDefault(page => page.Name == Resolve(name))?.SearchTerms ?? "";
}
