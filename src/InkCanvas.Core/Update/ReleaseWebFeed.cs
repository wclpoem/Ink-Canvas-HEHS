using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using InkCanvas.Core.Versioning;

namespace InkCanvas.Core.Update;

/// <summary>Public release pages remain accessible when the anonymous GitHub API is rate limited.</summary>
public static partial class ReleaseWebFeed
{
    public sealed record Entry(string Tag, string Name, string Notes);

    public static IReadOnlyList<Entry> ReadEntries(string html, string repository)
    {
        var entries = new List<Entry>();
        foreach (Match section in Sections().Matches(html))
        {
            string content = section.Value;
            if (Prerelease().IsMatch(content)) continue;
            var link = Links().Matches(content).FirstOrDefault(match => IsTagLink(match.Groups[1].Value, repository));
            if (link is null) continue;
            string path = new Uri(new Uri("https://github.com"), WebUtility.HtmlDecode(link.Groups[1].Value)).AbsolutePath;
            string tag = Uri.UnescapeDataString(path[("/" + repository + "/releases/tag/").Length..]);
            if (!AppVersion.TryParse(tag, out _)) continue;
            string name = PlainText(link.Groups[2].Value);
            var body = Notes().Match(content);
            entries.Add(new(tag, name, body.Success ? PlainText(body.Groups[1].Value) : ""));
        }
        return entries.DistinctBy(entry => entry.Tag).OrderByDescending(entry =>
        { AppVersion.TryParse(entry.Tag, out var version); return version; }).ToArray();
    }

    public static ReleaseInfo ReadAssets(string html, string repository, Entry entry, bool preferMsi = false)
        => ReleaseFeed.ParseLatest(ReadAssetsJson(html, repository, entry), preferMsi);

    public static string ReadAssetsJson(string html, string repository, Entry entry)
    {
        string prefix = "/" + repository + "/releases/download/" + Uri.EscapeDataString(entry.Tag) + "/";
        var assets = new List<object>();
        foreach (Match row in AssetRows().Matches(html))
        {
            var link = Links().Matches(row.Value).FirstOrDefault(match => IsAssetLink(match.Groups[1].Value, prefix));
            if (link is null) continue;
            var uri = new Uri(new Uri("https://github.com"), WebUtility.HtmlDecode(link.Groups[1].Value));
            string name = Uri.UnescapeDataString(uri.AbsolutePath[prefix.Length..]);
            string digest = Digest().Match(row.Value).Value;
            assets.Add(new { name, browser_download_url = uri.AbsoluteUri, digest });
        }
        // Reuse the same asset selection and digest validation as API releases.
        return JsonSerializer.Serialize(new
        {
            tag_name = entry.Tag, name = entry.Name, body = entry.Notes,
            html_url = "https://github.com/" + repository + "/releases/tag/" + Uri.EscapeDataString(entry.Tag),
            draft = false, prerelease = false, assets,
        });
    }

    public static bool IsAssetsPage(string html, string repository, Entry entry)
        => AssetRows().Matches(html).Any(row => Links().Matches(row.Value).Any(link => IsAssetLink(link.Groups[1].Value,
            "/" + repository + "/releases/download/" + Uri.EscapeDataString(entry.Tag) + "/")));

    private static bool IsTagLink(string link, string repository)
        => Uri.TryCreate(new Uri("https://github.com"), WebUtility.HtmlDecode(link), out var uri)
            && uri.Scheme == "https" && uri.Host == "github.com" && uri.UserInfo.Length == 0
            && uri.AbsolutePath.StartsWith("/" + repository + "/releases/tag/", StringComparison.OrdinalIgnoreCase);

    private static bool IsAssetLink(string link, string prefix)
        => Uri.TryCreate(new Uri("https://github.com"), WebUtility.HtmlDecode(link), out var uri)
            && uri.Scheme == "https" && uri.Host == "github.com" && uri.UserInfo.Length == 0
            && uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static string PlainText(string html) => WebUtility.HtmlDecode(Tags().Replace(html, " ")).Trim();

    [GeneratedRegex("<section\\b[^>]*>.*?</section>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Sections();
    [GeneratedRegex("<a\\b[^>]*href=[\"']([^\"']+)[\"'][^>]*>(.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Links();
    [GeneratedRegex(">\\s*Pre-release\\s*<", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Prerelease();
    [GeneratedRegex("<li\\b[^>]*>.*?</li>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex AssetRows();
    [GeneratedRegex("sha256:[0-9a-fA-F]{64}", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Digest();
    [GeneratedRegex("<div\\b[^>]*class=[\"'][^\"']*markdown-body[^\"']*[\"'][^>]*>(.*?)</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Notes();
    [GeneratedRegex("<[^>]*>", RegexOptions.Singleline, 1000)]
    private static partial Regex Tags();
}
