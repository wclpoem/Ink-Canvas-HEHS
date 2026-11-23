using System.ComponentModel;
using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Controls.Controls;
using InkCanvas.Controls.Lifecycle;
using InkCanvas.Ink.Rendering;
using InkCanvas.Pdf;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace InkCanvas.Modules.Pdf;

public sealed partial class PdfWindow
{
    private readonly CancellationTokenSource _thumbnailCancellation = new();
    private IReadOnlyList<PdfSearchHit> _hits = Array.Empty<PdfSearchHit>();
    private readonly TextBlock _searchCount = new() { Text = "输入文字后搜索", VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
    private readonly SelectorBar _navigation = new() { Margin = new Thickness(4, 6, 4, 6),
        Items = { new SelectorBarItem { Text = "页面" }, new SelectorBarItem { Text = "目录" }, new SelectorBarItem { Text = "搜索" } } };
    private int NavigationIndex
    {
        get => _navigation.Items.IndexOf(_navigation.SelectedItem);
        set => _navigation.SelectedItem = _navigation.Items[value];
    }
    private readonly SolidColorBrush _thumbnailBorder = new(), _thumbnailAccent = new();
    private ThumbnailItem _highlightedThumbnail;
    private readonly TextBlock _navigationEmpty = new() { Margin = new Thickness(20), TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private TouchIconButton _previousSearch, _nextSearch;
    private double _thumbnailScale;
    private readonly Dictionary<int, ThumbnailItem> _realizedThumbnails = new();
    private bool _sidebarUserSet;
    private TouchIconButton _sidebarToggle;
    private int _searchIndex = -1;
    public bool SidebarOpen => _split.IsPaneOpen;
    internal SplitViewDisplayMode SidebarMode => _split.DisplayMode;
    public int SearchResultCount => _hits.Count;
    public int RealizedThumbnailCount => _realizedThumbnails.Values.Count(item => item.Preview is not null);

    private sealed class ThumbnailItem(int index, PdfPageGeometry geometry, double scale, Brush frameBrush) : INotifyPropertyChanged
    {
        public int Index => index;
        public string Title => "第 " + (index + 1) + " 页";
        public string PageNumber => (index + 1).ToString();
        private Brush _frameBrush = frameBrush;
        public Brush FrameBrush
        {
            get => _frameBrush;
            set { if (ReferenceEquals(_frameBrush, value)) return; _frameBrush = value; PropertyChanged?.Invoke(this, new(nameof(FrameBrush))); }
        }
        public double Width => geometry.DipWidth * scale;
        public double Height => geometry.DipHeight * scale;
        public CancellationTokenSource Cancellation;
        private ImageSource _preview;
        public ImageSource Preview
        {
            get => _preview;
            set { _preview = value; PropertyChanged?.Invoke(this, new(nameof(Preview))); }
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    private void ConfigureSidebar()
    {
        // One document scale preserves both page aspect and relative physical page sizes.
        // Page-local annotation extents do not participate in thumbnail sizing.
        _thumbnailScale = Math.Min(156 / _layout.Width, 196 / _file.Pages.Max(page => page.DipHeight));
        _pages.ItemsSource = Enumerable.Range(0, _file.Pages.Count).Select(index => new ThumbnailItem(index, _file.Pages[index], _thumbnailScale, _thumbnailBorder)).ToArray();
        _pages.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <StackPanel Width="176" Spacing="6" Margin="0,8" UseLayoutRounding="False">
                    <Border HorizontalAlignment="Center" Background="White" BorderBrush="{Binding FrameBrush}" BorderThickness="2" CornerRadius="6" UseLayoutRounding="False">
                        <Image Width="{Binding Width}" Height="{Binding Height}" Source="{Binding Preview}" Stretch="Fill" UseLayoutRounding="False" />
                    </Border>
                    <Border Background="{ThemeResource SubtleFillColorSecondaryBrush}" CornerRadius="10" Padding="10,2" HorizontalAlignment="Center">
                        <TextBlock Text="{Binding PageNumber}" FontSize="11" />
                    </Border>
                </StackPanel>
            </DataTemplate>
            """);
        _pages.ContainerContentChanging += (sender, args) =>
        {
            if (args.Item is not ThumbnailItem item) return;
            item.Cancellation?.Cancel();
            item.Preview = null;
            if (args.InRecycleQueue) _realizedThumbnails.Remove(item.Index);
            else
            {
                _realizedThumbnails[item.Index] = item;
                _ = LoadThumbnailAsync(item);
            }
        };
        _sidebar.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _sidebar.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _sidebar.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _sidebar.Children.Add(_navigation);
        Grid.SetRow(_sidebarSearch, 1); _sidebar.Children.Add(_sidebarSearch);
        foreach (var content in new FrameworkElement[] { _pages, _bookmarks, _searchResults, _navigationEmpty })
        { Grid.SetRow(content, 2); _sidebar.Children.Add(content); }
        ConfigureFloatingSearch();
        _pages.Margin = _bookmarks.Margin = _searchResults.Margin = new Thickness(4, 0, 4, 0);
        foreach (string key in new[] { "ListViewItemBackgroundSelected", "ListViewItemBackgroundSelectedPointerOver", "ListViewItemBackgroundSelectedPressed" })
            _pages.Resources[key] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _pages.ItemContainerStyle = new Style(typeof(ListViewItem));
        _pages.ItemContainerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        _pages.ItemContainerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4)));
        _searchResults.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <TextBlock Text="{Binding}" TextWrapping="Wrap" MaxLines="5" Margin="4,8" />
            </DataTemplate>
            """);
        DeferredLayout.Observe(_root, UpdateSidebarLayout);
        AutomationProperties.SetName(_navigation, "PDF 导航视图");
        AutomationProperties.SetName(_navigationEmpty, "PDF 导航空状态");
        AutomationProperties.SetName(_searchCount, "PDF 搜索匹配计数");
        _navigation.SelectionChanged += (_, _) => UpdateSidebarView();
        NavigationIndex = 0;
        UpdateSearchNavigation();
        _split.PaneClosed += (_, _) => { _sidebarToggle.IsSelected = false; SuspendThumbnails(); };
        _split.PaneOpened += (_, _) => { _sidebarToggle.IsSelected = true; RefreshThumbnails(); };
    }

    private void UpdateSidebarView()
    {
        if (_closed) return;
        int selected = NavigationIndex;
        _pages.Visibility = selected == 0 ? Visibility.Visible : Visibility.Collapsed;
        _bookmarks.Visibility = selected == 1 && _file.Bookmarks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _sidebarSearch.Visibility = selected == 2 ? Visibility.Visible : Visibility.Collapsed;
        _searchResults.Visibility = selected == 2 && _hits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _navigationEmpty.Text = selected == 1 ? "此 PDF 没有目录书签" : _searching ? "正在搜索…"
            : string.IsNullOrWhiteSpace(_search.Text) ? "输入文字后搜索文档" : "没有匹配结果";
        _navigationEmpty.Visibility = selected == 1 && _file.Bookmarks.Count == 0 || selected == 2 && _hits.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        if (selected == 0) RefreshThumbnails();
    }

    private void UpdateSearchNavigation()
    {
        if (_closed) return;
        _previousSearch.IsEnabled = _nextSearch.IsEnabled = !_searching && _hits.Count > 0;
        _searchCount.Text = _searching ? "搜索中…" : _hits.Count > 0 ? (_searchIndex + 1) + " / " + _hits.Count
            : string.IsNullOrWhiteSpace(_search.Text) ? "输入关键词" : "无匹配结果 · 扫描件需先识别文字";
        UpdateSidebarView();
    }

    public void SetSidebarOpen(bool open)
    {
        _sidebarUserSet = true;
        _split.IsPaneOpen = open;
        _sidebarToggle.IsSelected = open;
    }

    private void UpdateSidebarLayout()
    {
        // On narrow touch windows an overlay pane preserves a usable page viewport.
        double width = _root.XamlRoot?.Size.Width ?? _root.ActualWidth;
        var mode = width < 760 ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
        if (_split.DisplayMode != mode)
        {
            _split.DisplayMode = mode;
            ApplyAppearance();
            // Entering the narrow layout releases the page width. The page button can then
            // open the pane over the document without resizing or refitting the page.
            _split.IsPaneOpen = false;
        }
        if (!_sidebarUserSet && width > 0) _split.IsPaneOpen = width >= 760;
        _sidebarToggle.IsSelected = _split.IsPaneOpen;
    }

    private void UpdateThumbnailSelection()
    {
        if (_pages.Items.Count == 0) return;
        var current = (ThumbnailItem)_pages.Items[_pageIndex];
        if (ReferenceEquals(current, _highlightedThumbnail)) return;
        if (_highlightedThumbnail is not null) _highlightedThumbnail.FrameBrush = _thumbnailBorder;
        current.FrameBrush = _thumbnailAccent;
        _highlightedThumbnail = current;
    }

    private void SuspendThumbnails()
    {
        foreach (var item in _realizedThumbnails.Values) { item.Cancellation?.Cancel(); item.Preview = null; }
    }

    private void RefreshThumbnails()
    {
        if (!_split.IsPaneOpen || _idleResources?.IsActive != true) return;
        foreach (var item in _realizedThumbnails.Values) _ = LoadThumbnailAsync(item);
    }

    private async Task LoadThumbnailAsync(ThumbnailItem item)
    {
        if (_closed || !_split.IsPaneOpen || _idleResources?.IsActive != true) return;
        item.Cancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_thumbnailCancellation.Token);
        item.Cancellation = cancellation;
        try
        {
            await Task.Delay(80, cancellation.Token);
            var geometry = _file.Pages[item.Index];
            double scale = _thumbnailScale;
            var bytes = await _rasterizer.RenderAsync(item.Index, geometry, new(0, 0, geometry.DipWidth, geometry.DipHeight), scale, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var strokes = _controllers.TryGetValue(item.Index, out var controller) ? controller.Document.Strokes : _file.GetInk(item.Index);
            using var stream = new InMemoryRandomAccessStream();
            if (strokes.Count > 0)
            {
                using var bitmap = await LoadBitmapAsync(bytes, cancellation.Token);
                using var target = new CanvasRenderTarget(bitmap.Device, (float)bitmap.Size.Width, (float)bitmap.Size.Height, 96);
                using var scene = new InkSceneRenderer();
                using (var draw = target.CreateDrawingSession())
                {
                    draw.Clear(Microsoft.UI.Colors.White); draw.DrawImage(bitmap);
                    var transform = Matrix3x2.CreateScale((float)bitmap.Size.Width / geometry.DipWidth, (float)bitmap.Size.Height / geometry.DipHeight);
                    foreach (var stroke in strokes) scene.Strokes.Draw(draw, stroke, transform);
                }
                await target.SaveAsync(stream, CanvasBitmapFileFormat.Png).AsTask(cancellation.Token);
            }
            else
            {
                using var writer = new DataWriter(stream.GetOutputStreamAt(0));
                writer.WriteBytes(bytes); await writer.StoreAsync().AsTask(cancellation.Token); writer.DetachStream();
            }
            stream.Seek(0);
            var image = new BitmapImage();
            await image.SetSourceAsync(stream).AsTask(cancellation.Token);
            if (!_closed && !cancellation.IsCancellationRequested) item.Preview = image;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed && !cancellation.IsCancellationRequested) Core.Diagnostics.FileLog.WriteLogToFile("PDF thumbnail: " + ex.Message); }
        finally
        {
            if (ReferenceEquals(item.Cancellation, cancellation)) item.Cancellation = null;
            cancellation.Dispose();
        }
    }

    private void ClearSearch()
    {
        _searchCancellation?.Cancel();
        _searching = false;
        _hits = Array.Empty<PdfSearchHit>(); _searchIndex = -1;
        _navigationPictureDirty = true;
        _searchResults.ItemsSource = null;
        _statusMessage = null;
        UpdateSearchNavigation(); UpdateDocumentStatus();
        _paper.Invalidate();
    }

    public void NextSearchResult(int direction)
    {
        if (_hits.Count == 0) return;
        _searchIndex = (_searchIndex + direction + _hits.Count) % _hits.Count;
        if (_searchResults.SelectedIndex == _searchIndex) ShowSearchHit(_hits[_searchIndex]);
        else _searchResults.SelectedIndex = _searchIndex;
        _searchResults.ScrollIntoView(_searchResults.SelectedItem);
    }

    private void ShowSearchHit(PdfSearchHit hit)
    {
        if (Controller.HasActiveInteraction) return;
        _searchIndex = _hits.ToList().IndexOf(hit);
        GoToPage(hit.PageIndex);
        if (hit.Bounds is { Count: > 0 })
        {
            var center = hit.Bounds[0].Center;
            var point = Vector2.Transform(center, Controller.ViewTransform);
            Controller.ViewTransform *= Matrix3x2.CreateTranslation((float)_viewport.ActualWidth / 2 - point.X, (float)_viewport.ActualHeight / 2 - point.Y);
            _fitMode = FitMode.Free;
            Controller.NotifyElementsChanged();
        }
        UpdateSearchNavigation();
        _paper.Invalidate();
    }

    private void DrawSearchHighlights(CanvasDrawingSession draw, int page)
    {
        foreach (var hit in _hits.Where(hit => hit.PageIndex == page))
        foreach (var bounds in hit.Bounds ?? Array.Empty<Core.Ink.Rect>())
        {
            bool selected = _searchIndex >= 0 && ReferenceEquals(_hits[_searchIndex], hit);
            draw.FillRectangle(bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                selected ? Windows.UI.Color.FromArgb(140, 255, 150, 0) : Windows.UI.Color.FromArgb(100, 255, 225, 0));
        }
    }
}
