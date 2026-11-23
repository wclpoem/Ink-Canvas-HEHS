using InkCanvas.Controls.Controls;
using InkCanvas.Host.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace InkCanvas.Modules.Library;

internal enum LibraryMode { Browse, Open, Save }

internal sealed partial class LibraryWindow : Window
{
    private readonly IBoardLibraryHost _host;
    private readonly LibraryStore _store;
    private readonly TaskCompletionSource<string> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Grid _root = new();
    private readonly ListView _locations = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly GridView _files = new() { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true };
    private readonly ListView _folders = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly AutoSuggestBox _search = new() { PlaceholderText = "搜索文稿和文件夹", MinWidth = 0 };
    private readonly ToggleSwitch _recursive = new() { Header = "搜索子文件夹", IsOn = true };
    private readonly ComboBox _sort = new() { ItemsSource = new[] { "最近修改", "名称 A–Z", "创建时间", "文件大小" }, SelectedIndex = 0, MinWidth = 0, Width = 124 };
    private readonly BreadcrumbBar _path = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _previewTitle = new() { FontSize = 20, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _previewDetails = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _pageText = new() { VerticalAlignment = VerticalAlignment.Center, Text = "页面预览" };
    private readonly TextBox _name = new() { Header = "文稿名称", MinWidth = 0 };
    private readonly Button _action = new() { MinWidth = 96, MinHeight = 44 };
    private readonly ProgressRing _busy = new() { Width = 20, Height = 20 };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _listing, _pageLoad, _aiLoad;
    private readonly Dictionary<SelectorItem, LibraryEntry> _containers = new();
    private readonly Dictionary<SelectorItem, CancellationTokenSource> _thumbnailLoads = new();
    private readonly IReadOnlyList<string> _extensions;
    private string _folder, _category = "文件夹";
    private int _page, _pageCount;
    private bool _closed, _loading, _committing, _ready, _selectingLocation;
    private LibraryEntry[] _entries = Array.Empty<LibraryEntry>();
    public LibraryMode Mode { get; }
    public Task<string> Result => _result.Task;

    public LibraryWindow(IBoardLibraryHost host, LibraryMode mode, string suggestedName, IReadOnlyList<string> extensions, LibraryStore store)
    {
        _host = host; Mode = mode; _extensions = extensions;
        _store = store;
        Title = mode == LibraryMode.Save ? "保存到文稿库" : "文稿库";
        _name.Text = System.IO.Path.GetFileNameWithoutExtension(suggestedName ?? "白板");
        _action.Content = mode == LibraryMode.Save ? "保存文稿" : "打开文稿";
        _action.IsEnabled = false;
        Build();
        Content = _root;
        host.ConfigureWindow(this);
        WindowFrame.Attach(this, _root).UseMica();
        _root.Loaded += async (_, _) => await RunAsync(InitializeAsync);
        Closed += (_, _) =>
        {
            _closed = true; _debounce.Stop(); _lifetime.Cancel(); _listing?.Cancel(); _pageLoad?.Cancel(); _aiLoad?.Cancel();
            _inspectorFlyout.Hide();
            foreach (var load in _thumbnailLoads.Values) load.Cancel();
            _thumbnailLoads.Clear(); _files.ItemsSource = null; _containers.Clear(); _preview.Source = null;
            _result.TrySetResult(null);
        };
    }

    private async Task InitializeAsync()
    {
        _loading = true;
        await _store.LoadAsync(); if (_closed) return;
        string defaultFolder = _host.DefaultDirectory;
        Directory.CreateDirectory(defaultFolder);
        if (!_store.State.Locations.Contains(defaultFolder, StringComparer.OrdinalIgnoreCase)) _store.State.Locations.Insert(0, defaultFolder);
        _folder = Directory.Exists(_store.State.LastFolder) ? _store.State.LastFolder : defaultFolder;
        _sort.SelectedIndex = Math.Clamp(_store.State.Sort, 0, 3);
        SetView(_store.State.ListView);
        UpdateLocations(); _loading = false; await RefreshAsync();
        _ready = true;
        if (!_closed) UpdateSelection();
    }

    private void UpdateLocations()
    {
        _selectingLocation = true;
        _locations.ItemsSource = new[] { new LibraryLocation("最近使用", null, "\uF486"), new LibraryLocation("收藏", null, "\uF719") };
        _folders.ItemsSource = _store.State.Locations.Select(path => new LibraryLocation(
            System.IO.Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : path, path, "\uF41D")).ToArray();
        _selectingLocation = false;
        SelectLocation();
    }

    private async Task RefreshAsync()
    {
        _listing?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _listing = request;
        _busy.IsActive = true; UpdateBreadcrumb();
        string selectedPath = (_files.SelectedItem as LibraryEntry)?.Path;
        try
        {
            var selected = _category == "收藏" ? _store.State.Favorites : _category == "最近使用" ? _store.State.Recent : null;
            var entries = await LibraryStore.ListAsync(_folder, selected?.ToArray(), _search.Text, _recursive.IsOn, request.Token);
            if (request.IsCancellationRequested || _closed) return;
            IEnumerable<LibraryEntry> ordered = _sort.SelectedIndex switch
            {
                1 => entries.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
                2 => entries.OrderByDescending(item => item.Created),
                3 => entries.OrderByDescending(item => item.Length),
                _ => _category == "最近使用" ? entries.OrderBy(item => _store.State.Recent.FindIndex(path => path.Equals(item.Path, StringComparison.OrdinalIgnoreCase)))
                    : entries.OrderByDescending(item => item.Modified),
            };
            _entries = ordered.OrderByDescending(item => item.IsFolder).ToArray();
            foreach (var entry in _entries) entry.Favorite = _store.State.Favorites.Contains(entry.Path, StringComparer.OrdinalIgnoreCase);
            _files.ItemsSource = _entries;
            _files.SelectedItem = _entries.FirstOrDefault(entry => entry.Path.Equals(selectedPath, StringComparison.OrdinalIgnoreCase));
            _empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _files.Visibility = entries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            _clearFilters.Visibility = string.IsNullOrWhiteSpace(_search.Text) ? Visibility.Collapsed : Visibility.Visible;
            _status.Text = entries.Count == 0 ? "" : $"{entries.Count(item => !item.IsFolder)} 份文稿 · {entries.Count(item => item.IsFolder)} 个文件夹";
            SelectLocation(); UpdateSelection();
            _store.State.LastFolder = _folder; _store.State.Sort = _sort.SelectedIndex;
            await _store.SaveAsync();
        }
        finally { if (ReferenceEquals(_listing, request)) { _listing = null; _busy.IsActive = false; } }
    }

    private void OnContainerChanged(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        var container = args.ItemContainer;
        container.ContextRequested -= OnEntryContextRequested;
        container.ContextRequested += OnEntryContextRequested;
        if (_thumbnailLoads.Remove(container, out var oldLoad)) oldLoad.Cancel();
        if (_containers.Remove(args.ItemContainer, out var previous)) previous.Preview = null;
        if (args.InRecycleQueue || args.Item is not LibraryEntry entry) return;
        if (container.ContentTemplateRoot is not FrameworkElement template)
        { args.RegisterUpdateCallback(OnContainerChanged); return; }
        if (template.FindName("FavoriteButton") is ButtonBase favorite)
        { favorite.Click -= OnCardFavoriteClick; favorite.Click += OnCardFavoriteClick; }
        if (template.FindName("CardPreview") is Border card)
        { card.SizeChanged -= OnThumbnailSizeChanged; card.SizeChanged += OnThumbnailSizeChanged; SizeThumbnail(card); }
        if (entry.IsFolder) return;
        _containers[container] = entry;
        var load = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _thumbnailLoads[container] = load;
        _ = RunAsync(async () =>
        {
            try
            {
                var result = await _store.PreviewAsync(entry.Path, 0, load.Token);
                if (_closed || load.IsCancellationRequested || !_containers.TryGetValue(container, out var current) || !ReferenceEquals(current, entry)) return;
                var bitmap = await BitmapAsync(result.Png);
                if (!load.IsCancellationRequested) { entry.Preview = bitmap; entry.Pages = result.Pages; }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            { /* A bad file must not interrupt scrolling. Selected preview reports the error. */ }
            finally { if (_thumbnailLoads.TryGetValue(container, out var current) && ReferenceEquals(current, load)) _thumbnailLoads.Remove(container); load.Dispose(); }
        });
    }

    private async Task LoadPageAsync()
    {
        _pageLoad?.Cancel();
        if (_files.SelectedItem is not LibraryEntry { IsFolder: false } entry) return;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _pageLoad = request;
        try
        {
            int page = _page;
            var result = await _store.PreviewAsync(entry.Path, page, request.Token);
            var bitmap = await BitmapAsync(result.Png);
            if (request.IsCancellationRequested || _closed) return;
            _preview.Source = bitmap;
            var metadata = await InkCanvas.Features.Board.BoardService.ReadArchiveMetadataAsync(entry.Path, request.Token);
            if (request.IsCancellationRequested || _closed) return;
            _description.Text = metadata?.Description ?? "";
            _description.Visibility = string.IsNullOrWhiteSpace(_description.Text) ? Visibility.Collapsed : Visibility.Visible;
            if (result.Pages > 0) _pageCount = result.Pages;
            _pageText.Text = _pageCount > 0 ? $"{_page + 1} / {_pageCount}" : $"第 {_page + 1} 页";
            entry.Pages = _pageCount; UpdatePageButtons();
        }
        finally { if (ReferenceEquals(_pageLoad, request)) _pageLoad = null; }
    }

    private static async Task<BitmapImage> BitmapAsync(byte[] png)
    {
        if (png is null) return null;
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(png); await writer.StoreAsync(); }
        stream.Seek(0); var bitmap = new BitmapImage { DecodePixelWidth = 768 }; await bitmap.SetSourceAsync(stream); return bitmap;
    }

    private async Task CommitAsync()
    {
        if (_committing || !_ready) return;
        string path;
        if (Mode == LibraryMode.Save)
        {
            path = LibraryStore.NamedPath(_folder, _name.Text, true);
            if (File.Exists(path) && !await ConfirmAsync("替换现有文稿？", System.IO.Path.GetFileName(path))) return;
        }
        else
        {
            if (_files.SelectedItem is not LibraryEntry { IsFolder: false } entry) return;
            path = entry.Path;
        }
        _committing = true; UpdateSelection();
        try { await CompleteAsync(path); }
        finally { _committing = false; if (!_closed) UpdateSelection(); }
    }

    private async Task CompleteAsync(string path)
    {
        if (Mode == LibraryMode.Browse && !await _host.OpenBoardAsync(path)) return;
        _store.Remember(path); await _store.SaveAsync();
        if (_closed) return;
        _result.TrySetResult(path); Close();
    }

    private async Task SystemPickerAsync()
    {
        string path;
        if (Mode == LibraryMode.Save)
        {
            var picker = new FileSavePicker { SuggestedFileName = _name.Text };
            picker.FileTypeChoices.Add("Ink Canvas 白板", new[] { ".icink" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            path = (await picker.PickSaveFileAsync())?.Path;
        }
        else
        {
            var picker = new FileOpenPicker();
            foreach (string extension in _extensions ?? new[] { ".icink" }) picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            path = (await picker.PickSingleFileAsync())?.Path;
        }
        if (path is not null && !_closed) await CompleteAsync(path);
    }

    private async Task AddLocationAsync()
    {
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync(); if (folder is null || _closed) return;
        if (!_store.State.Locations.Contains(folder.Path, StringComparer.OrdinalIgnoreCase)) _store.State.Locations.Add(folder.Path);
        _folder = folder.Path; _category = "文件夹"; UpdateLocations(); await RefreshAsync();
    }

    private async Task<string> AskNameAsync(string title, string initial)
    {
        var input = new TextBox { Text = initial, MinWidth = 260 };
        var dialog = new ContentDialog { Title = title, Content = input, PrimaryButtonText = "确定", CloseButtonText = "取消", XamlRoot = _root.XamlRoot };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? input.Text : null;
    }
    private async Task NewFolderAsync()
    {
        string name = await AskNameAsync("新建文件夹", "新文件夹"); if (name is null) return;
        string path = LibraryStore.NamedPath(_folder, name, false);
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("此名称已存在。");
        Directory.CreateDirectory(path); await RefreshAsync();
    }
    private async Task RenameAsync()
    {
        if (_files.SelectedItem is not LibraryEntry entry) return;
        string name = await AskNameAsync("重命名", entry.Name); if (name is null) return;
        string path = LibraryStore.NamedPath(System.IO.Path.GetDirectoryName(entry.Path), name, !entry.IsFolder);
        if (path == entry.Path) return;
        await Task.Run(() => { if (entry.IsFolder) Directory.Move(entry.Path, path); else File.Move(entry.Path, path); });
        RelocatePaths(entry.Path, path);
        UpdateLocations();
        await RefreshAsync();
        _files.SelectedItem = _entries.FirstOrDefault(entry => entry.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
    }
    private async Task TransferAsync(bool copy)
    {
        if (_files.SelectedItem is not LibraryEntry { IsFolder: false } entry) return;
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync(); if (folder is null || _closed) return;
        string destination = LibraryStore.NamedPath(folder.Path, entry.Name + (copy ? " 副本" : ""), true);
        if (destination.Equals(entry.Path, StringComparison.OrdinalIgnoreCase)) return;
        _busy.IsActive = true;
        try
        {
            if (copy)
            {
                string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await using (var input = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                    { await input.CopyToAsync(output, _lifetime.Token); await output.FlushAsync(_lifetime.Token); }
                    _lifetime.Token.ThrowIfCancellationRequested();
                    File.Move(temporary, destination, false);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            else
            {
                await Task.Run(() => File.Move(entry.Path, destination, false), _lifetime.Token);
                RelocatePaths(entry.Path, destination);
            }
            _store.Remember(destination); await _store.SaveAsync();
            await RefreshAsync(); _status.Text = copy ? "已制作副本" : "已移动文稿";
        }
        finally { _busy.IsActive = false; }
    }
    private void RelocatePaths(string source, string destination)
    {
        _store.RelocatePaths(source, destination);
        _folder = _store.State.LastFolder ?? _folder;
    }
    internal void Relocate(string previous, string path) => _ = RunAsync(async () =>
    {
        RelocatePaths(previous, path); await _store.SaveAsync(); await RefreshAsync();
    });
    private async Task<bool> ConfirmAsync(string title, string message) => await new ContentDialog
    { Title = title, Content = message, PrimaryButtonText = "替换", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, XamlRoot = _root.XamlRoot }.ShowAsync() == ContentDialogResult.Primary;

    private async Task RunAsync(Func<Task> action)
    {
        try { if (!_closed) await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) _status.Text = ex.GetBaseException().Message; }
    }
}
