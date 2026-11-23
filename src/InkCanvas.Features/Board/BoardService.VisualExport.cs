using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace InkCanvas.Features.Board;

public sealed partial class BoardService
{
    public static async IAsyncEnumerable<BoardService> ReadVisualPagesAsync(string path, [EnumeratorCancellation] CancellationToken token = default)
    {
        var model = await ReadBoardFileAsync(path, token, visualMetadataOnly: true).ConfigureAwait(false);
        using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        bool packaged = input.ReadByte() == 'P' && input.ReadByte() == 'K'; input.Position = 0;
        using var zip = packaged ? new ZipArchive(input, ZipArchiveMode.Read, true) : null;
        for (int index = 0; index < model.Pages.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var page = model.Pages[index];
            var board = new BoardService();
            try
            {
                if (zip is not null)
                {
                    long bytes = 0;
                    foreach (var blob in model.Blobs.Where(blob => blob.Page == index && blob.Kind != "video"))
                    {
                        var entry = zip.GetEntry(blob.Entry) ?? throw new InvalidDataException("页面媒体缺失。");
                        if ((bytes += entry.Length) > 192L * 1024 * 1024) throw new InvalidDataException("单页图像数据超过 192 MB，请拆分页面后重试。");
                        var data = new byte[entry.Length];
                        using var source = entry.Open(); await source.ReadExactlyAsync(data, token).ConfigureAwait(false);
                        if (blob.Kind == "image") page.Images[blob.Index].Data = data;
                        else page.Media[blob.Index].PosterData = data;
                    }
                }
                var prepared = PreparePage(page, ReadPaper(model.Background, model.GridSpacing, board._papers[board.Current])) with { Media = page.Media };
                board.ApplyBoard(new[] { prepared }); board.ArchiveMetadata = model.Archive;
                board.Current.ViewCenter = page.Viewport is { Length: 2 } && page.Viewport.All(float.IsFinite)
                    ? new(page.Viewport[0] / 2, page.Viewport[1] / 2) : new(960, 540);
                yield return board;
            }
            finally
            {
                foreach (var controller in board.Pages) controller.Dispose();
                foreach (var image in page.Images) image.Data = Array.Empty<byte>();
                foreach (var media in page.Media) { media.Data = Array.Empty<byte>(); media.PosterData = Array.Empty<byte>(); }
            }
        }
    }

    public static Task<string> DescribeArchiveAsync(string path, string title, string description, long expectedLength,
        DateTime expectedWriteUtc, CancellationToken token = default) => Task.Run(async () =>
    {
        string converted = await PrepareDescriptionSourceAsync(path, token).ConfigureAwait(false);
        bool locked = false;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await PackageCommitGate.WaitAsync(token).ConfigureAwait(false); locked = true;
            if (string.IsNullOrWhiteSpace(title) || title.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || title is "." or ".." || title.Length > 100)
                throw new ArgumentException("文稿标题不是有效的文件名。", nameof(title));
            var original = new FileInfo(path);
            if (!original.Exists || original.Length != expectedLength || original.LastWriteTimeUtc != expectedWriteUtc)
                throw new IOException("存档已被修改，此次 AI 结果已跳过，可针对最新文件重试。");
            using (var input = File.Open(converted ?? path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var source = new ZipArchive(input, ZipArchiveMode.Read))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                using (var target = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    foreach (var entry in source.Entries.Where(entry => entry.FullName != "archive.json" && entry.FullName != "preview/info.json"))
                    {
                        var replacement = target.CreateEntry(entry.FullName, entry.FullName.StartsWith("media/") || entry.FullName.EndsWith(".png") ? CompressionLevel.NoCompression : CompressionLevel.Fastest);
                        using var from = entry.Open(); using var to = replacement.Open();
                        await from.CopyToAsync(to, 65536, token).ConfigureAwait(false);
                    }
                    var metadata = source.GetEntry("archive.json") is { } old ? await ReadArchiveInfoAsync(old, token).ConfigureAwait(false) : null;
                    int pages = metadata?.PageCount ?? 1;
                    if (source.GetEntry("preview/info.json") is { } info)
                    {
                        using var details = info.Open();
                        var summary = await JsonSerializer.DeserializeAsync<BoardPreviewInfo>(details, cancellationToken: token).ConfigureAwait(false);
                        pages = summary?.PageCount ?? pages;
                    }
                    var updated = (metadata ?? new BoardArchiveMetadata { PageCount = pages }) with { Title = title, Description = description };
                    using (var details = target.CreateEntry("archive.json").Open()) await JsonSerializer.SerializeAsync(details, updated, cancellationToken: token).ConfigureAwait(false);
                    using (var details = target.CreateEntry("preview/info.json").Open())
                        await JsonSerializer.SerializeAsync(details, new BoardPreviewInfo(1, pages, title), cancellationToken: token).ConfigureAwait(false);
                }
                await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            for (int suffix = 0; ; suffix++)
            {
                string destination = Path.Combine(directory, title + (suffix == 0 ? "" : $" ({suffix + 1})") + ".icink");
                if (destination.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return path;
                try { File.Move(path, destination, false); return destination; }
                catch (IOException) when (File.Exists(destination)) { }
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                if (converted is not null && File.Exists(converted)) File.Delete(converted);
            }
            finally { if (locked) PackageCommitGate.Release(); }
        }
    }, token);

    private static async Task<string> PrepareDescriptionSourceAsync(string path, CancellationToken token)
    {
        using (var input = File.OpenRead(path)) if (input.ReadByte() == 'P' && input.ReadByte() == 'K') return null;
        var board = new BoardService();
        string converted = path + "." + Guid.NewGuid().ToString("N") + ".converted.icink";
        try
        {
            await board.LoadBoardAsync(path, token).ConfigureAwait(false);
            await board.SaveBoardAsync(converted, token).ConfigureAwait(false);
            return converted;
        }
        catch { if (File.Exists(converted)) File.Delete(converted); throw; }
        finally { foreach (var page in board.Pages) page.Dispose(); }
    }
}
