using InkCanvas.Core.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InkCanvas.Core.Update;

public sealed record ReleaseInfo(string Tag, AppVersion Version, string Name, string Notes,
    string PageUrl, string InstallerUrl, long InstallerSize)
{
    public string AssetName { get; init; }
    public string Sha256 { get; init; }
    public string ChecksumsUrl { get; init; }
    public bool IsPortable => AssetName?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true;
    public bool IsMsi => AssetName?.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) == true;
    public bool IsNewerThan(AppVersion current) => Version > current;
}

public static partial class ReleaseFeed
{
    /// <summary>Release attachments remain available through mirrors that cannot proxy GitHub's API or HTML.</summary>
    public static ReleaseInfo ParseLatestChecksums(string text, string repository, bool preferMsi = false)
    {
        var releases = new List<ReleaseInfo>();
        foreach (string line in text.Split('\n'))
        {
            var parts = line.Trim().Split((char[])null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || parts[0].Length != 64 || !parts[0].All(Uri.IsHexDigit)) continue;
            string name = parts[1].TrimStart('*', ' ');
            var match = (preferMsi ? MsiFileName() : PortableFileName()).Match(name);
            if (!match.Success || !AppVersion.TryParse(match.Groups[1].Value, out var version)) continue;
            string latest = $"https://github.com/{repository}/releases/latest";
            releases.Add(new("v" + version, version, "Ink Canvas WinUI 3 v" + version,
                "版本 API 暂不可用，已通过最新稳定版的校验清单获取更新信息。", latest,
                latest + "/download/" + Uri.EscapeDataString(name), 0)
            { AssetName = name, Sha256 = parts[0].ToLowerInvariant(), ChecksumsUrl = latest + "/download/SHA256SUMS.txt" });
        }
        return releases.OrderByDescending(release => release.Version).FirstOrDefault();
    }

    [GeneratedRegex("^InkCanvas-WinUI3-([vV]?[0-9]+\\.[0-9]+(?:\\.[0-9]+){0,2})-win-x64\\.zip$", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex PortableFileName();
    [GeneratedRegex("^InkCanvas-WinUI3-([vV]?[0-9]+\\.[0-9]+(?:\\.[0-9]+){0,2})-win-x64\\.msi$", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex MsiFileName();

    public static ReleaseInfo ParseLatest(string json, bool preferMsi = false)
    {
        using var document = Read(json);
        return document?.RootElement.ValueKind == JsonValueKind.Object ? Parse(document.RootElement, preferMsi) : null;
    }

    public static IReadOnlyList<ReleaseInfo> ParseStableList(string json, bool preferMsi = false)
    {
        using var document = Read(json);
        if (document?.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<ReleaseInfo>();
        return document.RootElement.EnumerateArray().Select(element => Parse(element, preferMsi)).Where(release => release is not null)
            .OrderByDescending(release => release.Version).ToArray();
    }

    public static ReleaseInfo ParseNewestStable(string json) => ParseStableList(json).FirstOrDefault();

    private static JsonDocument Read(string json)
    {
        try { return string.IsNullOrWhiteSpace(json) ? null : JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
    }

    private static ReleaseInfo Parse(JsonElement element, bool preferMsi)
    {
        if (element.ValueKind != JsonValueKind.Object || Bool(element, "draft") || Bool(element, "prerelease")) return null;
        string tag = Text(element, "tag_name");
        if (!AppVersion.TryParse(tag, out var version) || !element.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array) return null;
        var candidates = assets.EnumerateArray().Where(asset => asset.ValueKind == JsonValueKind.Object)
            .Select(asset => new { Asset = asset, Name = Text(asset, "name") }).ToArray();
        var package = candidates.Where(item => item.Name.EndsWith(preferMsi ? ".msi" : ".zip", StringComparison.OrdinalIgnoreCase)
                && item.Name.Contains("InkCanvas", StringComparison.OrdinalIgnoreCase)
                && item.Name.Contains("WinUI", StringComparison.OrdinalIgnoreCase)
                && item.Name.Contains("x64", StringComparison.OrdinalIgnoreCase)
                && !item.Name.Contains("arm64", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault() ?? candidates.Where(item => !preferMsi && item.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && !item.Name.Contains("arm64", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.Name.Contains("setup", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
        if (package is null || string.IsNullOrEmpty(Text(package.Asset, "browser_download_url"))) return null;
        string digest = Text(package.Asset, "digest");
        string hash = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null;
        return new(tag, version, Text(element, "name"), Text(element, "body"), Text(element, "html_url"),
            Text(package.Asset, "browser_download_url"), package.Asset.TryGetProperty("size", out var size)
                && size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out long length) ? length : 0)
        {
            AssetName = package.Name,
            Sha256 = hash is { Length: 64 } && hash.All(Uri.IsHexDigit) ? hash.ToLowerInvariant() : null,
            ChecksumsUrl = candidates.Where(item => item.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                .Select(item => Text(item.Asset, "browser_download_url")).FirstOrDefault(),
        };
    }

    private static string Text(JsonElement element, string name) => element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : "";
    private static bool Bool(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
