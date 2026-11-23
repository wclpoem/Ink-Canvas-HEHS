using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InkCanvas.Features.Board;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.Modules.Library;

internal sealed class LibraryEntry : INotifyPropertyChanged
{
    public string Path { get; init; }
    public string Name { get; init; }
    public bool IsFolder { get; init; }
    public long Length { get; init; }
    public DateTime Modified { get; init; }
    public DateTime Created { get; init; }
    public string Details => IsFolder ? "文件夹" : $"{Modified:g} · {Length / 1024d:N0} KB";
    public string Icon => IsFolder ? "\uF41D" : "\uF379";
    public Microsoft.UI.Xaml.Visibility FileVisibility => IsFolder ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    private int _pages;
    public int Pages { get => _pages; set { _pages = value; Changed(nameof(PageLabel)); } }
    public string PageLabel => Pages > 0 ? $"{Pages} 页" : "白板文稿";
    private bool _favorite;
    public bool Favorite { get => _favorite; set { _favorite = value; Changed(nameof(FavoriteGlyph)); Changed(nameof(FavoriteHint)); } }
    public string FavoriteGlyph => "\uF719";
    public string FavoriteHint => Favorite ? "取消收藏" : "收藏文稿";
    private ImageSource _preview;
    public ImageSource Preview { get => _preview; set { _preview = value; Changed(); } }
    public event PropertyChangedEventHandler PropertyChanged;
    private void Changed([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new(name));
}

internal sealed class LibraryState
{
    public List<string> Locations { get; set; } = new();
    public List<string> Favorites { get; set; } = new();
    public List<string> Recent { get; set; } = new();
    public string LastFolder { get; set; }
    public bool ListView { get; set; }
    public int Sort { get; set; }
}

internal sealed record LibraryLocation(string Title, string Path, string Icon) : INotifyPropertyChanged
{
    private bool _selected;
    public bool Selected
    {
        get => _selected;
        set { _selected = value; PropertyChanged?.Invoke(this, new(nameof(Indicator))); }
    }
    public Microsoft.UI.Xaml.Visibility Indicator => Selected ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public event PropertyChangedEventHandler PropertyChanged;
}

internal sealed class LibraryStore
{
    private readonly string _directory;
    private readonly SemaphoreSlim _previewGate = new(1);
    private readonly SemaphoreSlim _stateGate = new(1);
    public LibraryState State { get; private set; } = new();
    private Task _loading;
    public LibraryStore(string directory) => _directory = directory;

    public Task LoadAsync() => _loading ??= LoadCoreAsync();
    private async Task LoadCoreAsync()
    {
        try
        {
            using var stream = File.OpenRead(System.IO.Path.Combine(_directory, "library.json"));
            State = await JsonSerializer.DeserializeAsync<LibraryState>(stream) ?? new();
            State.Locations ??= new(); State.Favorites ??= new(); State.Recent ??= new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { State = new(); }
    }

    public async Task SaveAsync()
    {
        await _stateGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_directory);
            var snapshot = new LibraryState { Locations = State.Locations.ToList(), Favorites = State.Favorites.ToList(), Recent = State.Recent.ToList(), LastFolder = State.LastFolder,
                ListView = State.ListView, Sort = State.Sort };
            string temporary = System.IO.Path.Combine(_directory, "library.json.tmp");
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
                await JsonSerializer.SerializeAsync(stream, snapshot);
            File.Move(temporary, System.IO.Path.Combine(_directory, "library.json"), true);
        }
        finally { _stateGate.Release(); }
    }

    public static Task<List<LibraryEntry>> ListAsync(string folder, IEnumerable<string> selectedPaths, string query, bool recursive, CancellationToken token) => Task.Run(() =>
    {
        var paths = selectedPaths ?? Directory.EnumerateFileSystemEntries(folder, "*", new EnumerationOptions
        {
            RecurseSubdirectories = recursive && !string.IsNullOrWhiteSpace(query),
            IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
        });
        var result = new List<LibraryEntry>();
        foreach (string path in paths)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists && !Directory.Exists(path)) continue;
                bool isFolder = (info.Attributes & FileAttributes.Directory) != 0;
                if (!isFolder && !System.IO.Path.GetExtension(path).Equals(".icink", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrWhiteSpace(query) && !info.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)) continue;
                result.Add(new() { Path = info.FullName, Name = isFolder ? info.Name : System.IO.Path.GetFileNameWithoutExtension(info.Name),
                    IsFolder = isFolder, Length = isFolder ? 0 : info.Length, Modified = info.LastWriteTime, Created = info.CreationTime });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }, token);

    public void Remember(string path)
    {
        State.Recent.RemoveAll(value => value.Equals(path, StringComparison.OrdinalIgnoreCase));
        State.Recent.Insert(0, path);
        if (State.Recent.Count > 100) State.Recent.RemoveRange(100, State.Recent.Count - 100);
    }
    public void RelocatePaths(string source, string destination)
    {
        string Relocate(string path)
        {
            if (path is null) return null;
            if (path.Equals(source, StringComparison.OrdinalIgnoreCase)) return destination;
            return path.StartsWith(source.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ? destination + path[source.Length..] : path;
        }
        State.Favorites = State.Favorites.Select(Relocate).ToList();
        State.Recent = State.Recent.Select(Relocate).ToList();
        State.Locations = State.Locations.Select(Relocate).ToList();
        State.LastFolder = Relocate(State.LastFolder);
    }

    public async Task<(int Pages, byte[] Png)> PreviewAsync(string path, int page, CancellationToken token)
    {
        await _previewGate.WaitAsync(token);
        try
        {
            var file = new FileInfo(path);
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{page}")));
            string cache = System.IO.Path.Combine(_directory, "Previews", key + ".png");
            int pages = 0;
            if (page == 0)
            {
                var embedded = await BoardPreview.ReadAsync(path, token);
                pages = embedded.Info?.PageCount ?? 0;
                if (embedded.Cover is not null) return (pages, embedded.Cover);
            }
            if (File.Exists(cache) && File.Exists(cache + ".pages") && new FileInfo(cache).Length <= BoardPreview.MaximumCoverBytes)
            {
                if (int.TryParse(await File.ReadAllTextAsync(cache + ".pages", token), out int cachedPages)) pages = cachedPages;
                return (pages, await File.ReadAllBytesAsync(cache, token));
            }
            var rendered = await BoardService.ReadPagePreviewAsync(path, page, token);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(cache));
            await File.WriteAllBytesAsync(cache, rendered.Png, token);
            await File.WriteAllTextAsync(cache + ".pages", rendered.PageCount.ToString(), token);
            await Task.Run(() =>
            {
                long size = 0;
                foreach (var entry in new DirectoryInfo(System.IO.Path.GetDirectoryName(cache)).EnumerateFiles("*.png").OrderByDescending(info => info.LastWriteTimeUtc))
                {
                    size += entry.Length;
                    if (size > 128L * 1024 * 1024) try { entry.Delete(); File.Delete(entry.FullName + ".pages"); } catch (IOException) { }
                }
            }, token);
            return rendered;
        }
        finally { _previewGate.Release(); }
    }

    public static string NamedPath(string folder, string name, bool document)
    {
        name = name.Trim();
        if (document && name.EndsWith(".icink", StringComparison.OrdinalIgnoreCase)) name = name[..^6];
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0
            || name.EndsWith('.') || name.EndsWith(' ')) throw new ArgumentException("请输入有效名称，避免斜线、冒号和末尾的点。");
        string stem = name.Split('.')[0].ToUpperInvariant();
        if (new[] { "CON", "PRN", "AUX", "NUL" }.Contains(stem)
            || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])) throw new ArgumentException("此名称由 Windows 保留，请使用其他名称。");
        return System.IO.Path.Combine(folder, name + (document ? ".icink" : ""));
    }
}
