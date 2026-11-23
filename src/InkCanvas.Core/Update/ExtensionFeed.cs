using System.Text.Json;
using System.Text.RegularExpressions;
using InkCanvas.Core.Versioning;

namespace InkCanvas.Core.Update;

public sealed record RepositoryAsset(string Name, string Url, long Size, string Sha256, string ChecksumsUrl);
public enum RepositoryExtensionKind { Module, Plugin, Explorer }
public sealed record ExtensionRelease(string Id, string Name, string Description, string Version,
    RepositoryExtensionKind Kind, AppVersion HostVersion, string ReleaseTag, RepositoryAsset Asset);

public static partial class ExtensionFeed
{
    public static readonly IReadOnlyDictionary<string, (string Name, string Description, RepositoryExtensionKind Kind)> Known =
        new Dictionary<string, (string, string, RepositoryExtensionKind)>(StringComparer.Ordinal)
        {
            ["inkcanvas.ai-assistant"] = ("AI 助手", "教师通用助手、附件、多会话与工具调用", RepositoryExtensionKind.Module),
            ["inkcanvas.mcp"] = ("MCP 工具连接", "连接外部 MCP 服务并共享应用与插件工具", RepositoryExtensionKind.Module),
            ["inkcanvas.cloud-sync"] = ("云同步", "账号登录、白板云端备份和个人偏好同步", RepositoryExtensionKind.Module),
            ["inkcanvas.pdf"] = ("PDF 阅读与批注", "原生 PDF 阅读、搜索与墨迹保存", RepositoryExtensionKind.Module),
            ["inkcanvas.analytic-geometry"] = ("解析几何", "代数计算与二维、三维几何作图", RepositoryExtensionKind.Module),
            ["inkcanvas.library"] = ("文稿库", "文件夹导航、预览与白板文稿管理", RepositoryExtensionKind.Module),
            ["inkcanvas.screen-tracking"] = ("屏幕内容跟随", "滚动、缩放和页面批注记忆", RepositoryExtensionKind.Module),
            ["inkcanvas.time-machine"] = ("板书时光机", "课堂录制、章节书签与视频导出", RepositoryExtensionKind.Module),
            ["inkcanvas.legacy-import"] = ("旧版文稿导入", "导入历史版本的笔迹文件", RepositoryExtensionKind.Plugin),
            ["sample.guide-lines"] = ("参考线示例", "在画布中插入参考线", RepositoryExtensionKind.Plugin),
            ["inkcanvas.shell-preview"] = ("资源管理器预览", "白板文件缩略图与资源管理器预览", RepositoryExtensionKind.Explorer),
        };

    [GeneratedRegex(@"\A(?<id>[a-z0-9][a-z0-9.-]{2,63})-(?<version>[0-9]+\.[0-9]+(?:\.[0-9]+){0,2})(?:-win-x64)?\.zip\z", RegexOptions.CultureInvariant)]
    private static partial Regex PackageName();

    public static IReadOnlyList<ExtensionRelease> Parse(string json, string repository)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("插件版本列表无效。");
        var releases = new List<ExtensionRelease>();
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object || Flag(release, "draft") || Flag(release, "prerelease")
                || !AppVersion.TryParse(Text(release, "tag_name"), out var host)
                || !release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
            string checksums = assets.EnumerateArray().Where(asset => Text(asset, "name") == "SHA256SUMS.txt")
                .Select(asset => Text(asset, "browser_download_url")).FirstOrDefault();
            foreach (var asset in assets.EnumerateArray())
            {
                string name = Text(asset, "name"), url = Text(asset, "browser_download_url");
                var match = PackageName().Match(name);
                if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out _)
                    || !Known.TryGetValue(match.Groups["id"].Value, out var metadata) || !Trusted(url, repository)) continue;
                string digest = Text(asset, "digest");
                string hash = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null;
                if (hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit)) hash = null;
                long size = asset.TryGetProperty("size", out var length) && length.ValueKind == JsonValueKind.Number && length.TryGetInt64(out long value) ? value : 0;
                releases.Add(new(match.Groups["id"].Value, metadata.Name, metadata.Description, match.Groups["version"].Value,
                    metadata.Kind, host, Text(release, "tag_name"), new(name, url, size, hash, Trusted(checksums, repository) ? checksums : null)));
            }
        }
        return releases.GroupBy(item => item.Id).Select(group => group.OrderByDescending(item => Version.Parse(item.Version))
            .ThenByDescending(item => item.HostVersion).First()).OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
    }

    public static bool Trusted(string url, string repository) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host == "github.com" && string.IsNullOrEmpty(uri.UserInfo)
        && uri.AbsolutePath.StartsWith("/" + repository + "/releases/download/", StringComparison.OrdinalIgnoreCase);
    private static string Text(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : "";
    private static bool Flag(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
