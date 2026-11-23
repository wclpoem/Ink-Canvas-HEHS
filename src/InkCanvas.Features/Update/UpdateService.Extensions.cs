using System.Text.Json;
using InkCanvas.Core.Update;

namespace InkCanvas.Features.Update;

public sealed partial class UpdateService
{
    public async Task<IReadOnlyList<ExtensionRelease>> GetExtensionsAsync(CancellationToken token = default)
    {
        try
        {
            string text = await _transport.GetTextAsync($"https://api.github.com/repos/{Repository}/releases?per_page=100", content =>
            {
                try { using var document = JsonDocument.Parse(content); return document.RootElement.ValueKind == JsonValueKind.Array; }
                catch (JsonException) { return false; }
            }, token).ConfigureAwait(false);
            return ExtensionFeed.Parse(text, Repository);
        }
        catch (HttpRequestException) when (!token.IsCancellationRequested)
        {
            string page = await _transport.GetTextAsync($"https://github.com/{Repository}/releases",
                text => ReleaseWebFeed.ReadEntries(text, Repository).Count > 0, token).ConfigureAwait(false);
            var releases = new List<string>();
            foreach (var entry in ReleaseWebFeed.ReadEntries(page, Repository))
            {
                string assets = await _transport.GetTextAsync($"https://github.com/{Repository}/releases/expanded_assets/{Uri.EscapeDataString(entry.Tag)}",
                    text => ReleaseWebFeed.IsAssetsPage(text, Repository, entry), token).ConfigureAwait(false);
                releases.Add(ReleaseWebFeed.ReadAssetsJson(assets, Repository, entry));
            }
            return ExtensionFeed.Parse("[" + string.Join(",", releases) + "]", Repository);
        }
    }
}
