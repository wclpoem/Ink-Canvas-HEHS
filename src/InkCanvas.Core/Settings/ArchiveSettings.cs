using System.Globalization;
using System.Text.RegularExpressions;

namespace InkCanvas.Core.Settings;

public sealed class ArchiveSettings
{
    private ScreenArchiveSettings _screenCapture = new();
    public ScreenArchiveSettings ScreenCapture { get => _screenCapture ??= new(); set => _screenCapture = value ?? new(); }
    public string DefaultDirectory { get; set; } = ArchivePolicy.DefaultDirectory;
    public string AutomaticDirectory { get; set; } = "";
    public string Title { get; set; } = "白板";
    public string ClassName { get; set; } = "";
    public string Teacher { get; set; } = "";
    public string FileNameTemplate { get; set; } = "{title}_{date}_{time}_{reason}";
    public bool AutoSaveEnabled { get; set; }
    public int AutoSaveMinutes { get; set; } = 5;
    public bool SaveOnExit { get; set; }
    public bool OnlyWhenChanged { get; set; } = true;
    public bool SkipEmptyBoards { get; set; } = true;
    public bool UseDateFolders { get; set; } = true;
    public int AutomaticCopiesToKeep { get; set; } = 20;
}

public sealed class ScreenArchiveSettings
{
    public bool Enabled { get; set; }
    public bool IncludeOrdinaryAnnotation { get; set; }
    public string Directory { get; set; } = "";
    public int IntervalMinutes { get; set; } = 5;
    public bool SaveOnMouseMode { get; set; } = true;
    public bool SaveOnClear { get; set; } = true;
    public bool SaveOnExit { get; set; } = true;
    public bool OnlyWhenChanged { get; set; } = true;
    public int CopiesToKeep { get; set; } = 20;
}

public static class ArchivePolicy
{
    private static readonly Regex Tokens = new(@"\{([^{}]+)\}", RegexOptions.CultureInvariant);
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) is { Length: > 0 } documents
            ? documents : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents"),
        "InkCanvas", "Archives");

    public static string ResolveDirectory(string value)
    {
        string path = Environment.ExpandEnvironmentVariables((value ?? "").Trim());
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("请设置完整的存档目录，例如 D:\\教学存档。");
        return Path.GetFullPath(path);
    }

    public static string AutomaticDirectory(ArchiveSettings settings) => string.IsNullOrWhiteSpace(settings.AutomaticDirectory)
        ? Path.Combine(ResolveDirectory(settings.DefaultDirectory), "AutoSave") : ResolveDirectory(settings.AutomaticDirectory);

    public static string FileName(ArchiveSettings settings, DateTimeOffset savedAt, int pages, string reason)
    {
        string template = settings.FileNameTemplate?.Trim();
        if (string.IsNullOrEmpty(template) || template.Length > 240)
            throw new ArgumentException("文件名模板应包含 1 至 240 个字符。");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["title"] = settings.Title ?? "白板", ["class"] = settings.ClassName ?? "", ["teacher"] = settings.Teacher ?? "",
            ["date"] = savedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["time"] = savedAt.ToString("HH-mm-ss", CultureInfo.InvariantCulture),
            ["pages"] = pages.ToString(CultureInfo.InvariantCulture), ["reason"] = reason ?? "保存",
        };
        string fileName = Tokens.Replace(template, match => values.TryGetValue(match.Groups[1].Value, out string value)
            ? value : throw new ArgumentException("未知的文件名字段 " + match.Value));
        if (fileName.Contains('{') || fileName.Contains('}')) throw new ArgumentException("文件名模板中的花括号不完整。");
        var invalid = Path.GetInvalidFileNameChars();
        fileName = new string(fileName.Select(character => char.IsControl(character) || invalid.Contains(character) ? '_' : character).ToArray()).Trim().TrimEnd('.', ' ');
        if (fileName.EndsWith(".icink", StringComparison.OrdinalIgnoreCase)) fileName = fileName[..^6];
        if (fileName.Length > 160) fileName = fileName[..160].TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "白板";
        string firstPart = fileName.Split('.')[0];
        if (Regex.IsMatch(firstPart, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            fileName = "_" + fileName;
        return fileName + ".icink";
    }
}
