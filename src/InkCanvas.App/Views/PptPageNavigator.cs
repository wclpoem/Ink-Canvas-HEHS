using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Numerics;
using InkCanvas.App.Services;
using InkCanvas.Controls.Controls;
using InkCanvas.Features.Ppt;
using InkCanvas.Ink.Rendering;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace InkCanvas.App.Views;

internal sealed class PptPageThumbnail(PptSlideInfo slide) : INotifyPropertyChanged
{
    public PptSlideInfo Slide { get; set; } = slide;
    public string Title => $"第 {Slide.Index} 页";
    private ImageSource _preview;
    private string _status = "正在载入…";
    public ImageSource Preview { get => _preview; set { _preview = value; Notify(nameof(Preview)); } }
    public string Status { get => _status; set { _status = value; Notify(nameof(Status)); } }
    public bool Dirty = true;
    public bool Failed;
    public bool NeedsPreview => !Failed && (Dirty || Preview is null);
    public int Revision;
    public long LastUsed;
    public event PropertyChangedEventHandler PropertyChanged;
    private void Notify(string name) => PropertyChanged?.Invoke(this, new(name));
}

/// <summary>Virtualizes slide thumbnails and composites saved/current ink into each visible preview.</summary>
internal sealed class PptPageNavigator : UserControl, IDisposable
{
    private static readonly SemaphoreSlim CompositionGate = new(2, 2);
    private readonly PptPreviewContext _context;
    private readonly PptSlidePreviewSource _images;
    private readonly ObservableCollection<PptPageThumbnail> _items = new();
    private readonly ListView _list = new();
    private readonly GridView _grid = new();
    private readonly TouchIconButton _overviewButton = new()
    {
        Label = "总览",
        Glyph = Glyphs.Tools,
        Width = 56,
        Height = 48,
        IconSize = 20,
        IconAreaHeight = 24,
        LabelFontSize = 11
    };
    private readonly Dictionary<SelectorItem, PptPageThumbnail> _containers = new();
    private readonly HashSet<ScrollViewer> _scrolls = new();
    private readonly PopupHeader _header;
    private readonly TextBlock _status = new() { Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherQueueTimer _refresh;
    private CancellationTokenSource _loading;
    private CancellationTokenSource _viewportLoading;
    private PptSnapshot _snapshot;
    private Matrix3x2 _lastView;
    private bool _open, _disposed, _syncing, _rendering;
    private long _clock;
    public event Action CloseRequested;
    public event Action ViewModeChanged;
    internal bool IsOverview { get; private set; }
    internal ListViewBase ActiveView => IsOverview ? _grid : _list;
    internal ListView List => _list;
    internal int CachedThumbnailCount => _items.Count(item => item.Preview is not null);
    internal int ItemCount => _items.Count;
    internal int ExportCount => _images.ExportCount;
    internal int CachedSourceCount => _images.CachedCount;
    internal long CachedSourceBytes => _images.CachedBytes;

    public PptPageNavigator(PptPreviewContext context, int theme)
    {
        _context = context;
        _images = context.Images();
        _header = new PopupHeader("幻灯片预览", theme, canPin: false);
        _header.CloseRequested += () => CloseRequested?.Invoke();
        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(_header);
        _overviewButton.Click += (_, _) => SetOverview(!IsOverview);
        Grid.SetRow(_overviewButton, 1); root.Children.Add(_overviewButton);
        _list.ItemsSource = _items;
        _list.SelectionMode = ListViewSelectionMode.Single;
        _list.IsItemClickEnabled = true;
        _list.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <StackPanel Width="192" Spacing="6" Margin="4,8">
                    <Border Height="108" Background="{ThemeResource CardBackgroundFillColorDefaultBrush}" CornerRadius="4"
                            BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}" BorderThickness="1">
                        <Grid>
                            <Image Source="{Binding Preview}" Stretch="Uniform" />
                            <TextBlock Text="{Binding Status}" HorizontalAlignment="Center" VerticalAlignment="Center" FontSize="12" />
                        </Grid>
                    </Border>
                    <TextBlock Text="{Binding Title}" HorizontalAlignment="Center" FontSize="13" />
                </StackPanel>
            </DataTemplate>
            """);
        _list.ItemContainerStyle = new Style(typeof(ListViewItem));
        _list.ItemContainerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        _list.ItemContainerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(2)));
        ScrollViewer.SetHorizontalScrollMode(_list, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _grid.ItemsPanel = (ItemsPanelTemplate)XamlReader.Load("""
            <ItemsPanelTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <ItemsWrapGrid Orientation="Horizontal" />
            </ItemsPanelTemplate>
            """);
        _grid.ItemTemplate = _list.ItemTemplate;
        _grid.ItemContainerStyle = new Style(typeof(GridViewItem));
        _grid.ItemContainerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        _grid.ItemContainerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(2)));
        _grid.ItemsSource = _items;
        _grid.SelectionMode = ListViewSelectionMode.Single;
        _grid.IsItemClickEnabled = true;
        _grid.Visibility = Visibility.Collapsed;
        foreach (var view in new ListViewBase[] { _list, _grid })
        {
            view.ContainerContentChanging += (_, args) =>
            {
                if (args.InRecycleQueue) _containers.Remove(args.ItemContainer);
                else if (args.Item is PptPageThumbnail item) { _containers[args.ItemContainer] = item; Schedule(); }
            };
            view.Loaded += (_, _) =>
            {
                var scroll = Descendants(view).OfType<ScrollViewer>().FirstOrDefault();
                if (scroll is not null && _scrolls.Add(scroll)) scroll.ViewChanged += OnScrollChanged;
                Schedule();
            };
            view.SizeChanged += (_, _) => Schedule();
            view.ItemClick += (_, args) =>
            {
                if (!_open || _syncing || args.ClickedItem is not PptPageThumbnail item || _snapshot?.Presentation is null) return;
                if (_context.Navigate(_snapshot.Presentation, item.Slide)) CloseRequested?.Invoke();
            };
            view.SelectionChanged += (_, _) =>
            {
                if (!_open || _syncing || view.SelectedItem is not PptPageThumbnail item
                    || _snapshot?.Presentation is null || _snapshot.Slide?.Index == item.Slide.Index) return;
                if (_context.Navigate(_snapshot.Presentation, item.Slide)) CloseRequested?.Invoke();
            };
        }
        Grid.SetRow(_list, 2); root.Children.Add(_list);
        Grid.SetRow(_grid, 2); root.Children.Add(_grid);
        Grid.SetRow(_status, 3); root.Children.Add(_status);
        Content = root;
        ApplyTheme(theme);
        _refresh = DispatcherQueue.CreateTimer();
        _refresh.Interval = TimeSpan.FromMilliseconds(180);
        _refresh.IsRepeating = false;
        _refresh.Tick += (_, _) => _ = RenderVisibleAsync();
        context.Controller.Document.Changed += InvalidateCurrent;
        context.Controller.Invalidated += OnControllerInvalidated;
        _lastView = context.Controller.ViewTransform;
    }

    public void ApplyTheme(int theme)
    {
        RequestedTheme = theme == 2 ? ElementTheme.Dark : ElementTheme.Light;
        _header.ApplyTheme(theme);
    }

    internal void SetOverview(bool overview)
    {
        if (IsOverview == overview) return;
        IsOverview = overview;
        _overviewButton.Label = overview ? "返回列表" : "总览";
        _overviewButton.Glyph = overview ? Glyphs.SlideList : Glyphs.Tools;
        _list.Visibility = overview ? Visibility.Collapsed : Visibility.Visible;
        _grid.Visibility = overview ? Visibility.Visible : Visibility.Collapsed;
        CancelViewport();
        ViewModeChanged?.Invoke();
        SelectCurrent(); Schedule();
    }

    public void SetOpen(bool open, PptSnapshot snapshot)
    {
        _open = open;
        CancelViewport();
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = null;
        if (!open)
        {
            _refresh.Stop();
            foreach (var item in _items) item.Preview = null;
            return;
        }
        _snapshot = snapshot;
        _loading = new CancellationTokenSource();
        _items.Clear(); _containers.Clear();
        for (int index = 1; index <= snapshot.Presentation.SlideCount; index++)
        {
            var slide = snapshot.Slide?.Index == index ? snapshot.Slide : new PptSlideInfo(0, index, "", null, null, null);
            _items.Add(new(slide));
        }
        _status.Text = $"共 {_items.Count} 页";
        SelectCurrent();
        Schedule();
    }

    public void Update(PptSnapshot snapshot)
    {
        if (!_open) return;
        if (snapshot?.IsSlideShowRunning != true) { CloseRequested?.Invoke(); return; }
        if (_snapshot?.Presentation?.Identity != snapshot.Presentation?.Identity
            || _snapshot?.Presentation?.SlideCount != snapshot.Presentation?.SlideCount)
        {
            SetOpen(true, snapshot);
            return;
        }
        bool changed = _snapshot.Slide?.SlideId != snapshot.Slide?.SlideId;
        _snapshot = snapshot;
        if (changed) SelectCurrent();
    }

    private void SelectCurrent()
    {
        _syncing = true;
        try
        {
            _list.SelectedItem = _grid.SelectedItem = _items.FirstOrDefault(item => item.Slide.Index == _snapshot?.Slide?.Index);
            if (ActiveView.SelectedItem is not null) ActiveView.ScrollIntoView(ActiveView.SelectedItem);
        }
        finally { _syncing = false; }
    }

    private void OnControllerInvalidated()
    {
        var view = _context.Controller.ViewTransform;
        if (view == _lastView) return;
        _lastView = view;
        foreach (var item in _items) { item.Dirty = true; item.Revision++; }
        Schedule();
    }

    private void InvalidateCurrent()
    {
        var item = _items.FirstOrDefault(value => value.Slide.SlideId == _snapshot?.Slide?.SlideId);
        if (item is null) return;
        item.Dirty = true; item.Revision++;
        Schedule();
    }

    private void Schedule()
    {
        if (_disposed || !_open) return;
        _refresh.Stop(); _refresh.Start();
    }

    private async Task RenderVisibleAsync()
    {
        if (_disposed || !_open || _rendering || _loading is null) return;
        if (_context.Controller.HasActiveInteraction) { Schedule(); return; }
        _rendering = true;
        _viewportLoading = CancellationTokenSource.CreateLinkedTokenSource(_loading.Token);
        var viewport = _viewportLoading;
        var token = viewport.Token;
        var presentation = _snapshot.Presentation;
        try
        {
            foreach (var item in VisibleItems().OrderBy(item => Math.Abs(item.Slide.Index - (_snapshot.Slide?.Index ?? 1))).ToArray())
            {
                token.ThrowIfCancellationRequested();
                if (!item.NeedsPreview) continue;
                int revision = item.Revision;
                try
                {
                    var page = await _images.GetPageAsync(presentation, item.Slide.Index, token);
                    token.ThrowIfCancellationRequested();
                    if (!VisibleItems().Contains(item)) continue;
                    item.Slide = page.Slide;
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromMilliseconds(1500));
                    BitmapImage bitmap;
                    try
                    {
                        var ink = await _context.CaptureInk(presentation, item.Slide.SlideId, deadline.Token).WaitAsync(deadline.Token);
                        bitmap = await RenderThumbnailAsync(presentation, page.Image, ink, deadline.Token).WaitAsync(deadline.Token);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException(); }
                    token.ThrowIfCancellationRequested();
                    if (item.Revision != revision || !VisibleItems().Contains(item)) continue;
                    item.Preview = bitmap; item.Status = ""; item.Dirty = false; item.LastUsed = ++_clock;
                    int cacheLimit = Math.Max(24, VisibleItems().Count());
                    foreach (var expired in _items.Where(value => value.Preview is not null).OrderByDescending(value => value.LastUsed).Skip(cacheLimit))
                        expired.Preview = null;
                }
                catch (OperationCanceledException) { throw; }
                catch (TimeoutException) { item.Status = "预览超时，已跳过"; item.Dirty = false; item.Failed = true; }
                catch { item.Status = "预览暂不可用"; item.Dirty = false; item.Failed = true; }
                await Task.Delay(8, token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _rendering = false;
            if (ReferenceEquals(_viewportLoading, viewport)) _viewportLoading = null;
            viewport.Dispose();
            if (_open && VisibleItems().Any(item => item.NeedsPreview)) Schedule();
        }
    }

    internal static async Task<BitmapImage> RenderThumbnailAsync(PptPresentationInfo presentation, byte[] source, PptSlidePreviewInk ink,
        CancellationToken token = default)
    {
        byte[] pixels = await Task.Run(async () =>
        {
            await CompositionGate.WaitAsync(token);
            try
            {
                using var memory = new MemoryStream(source, writable: false);
                using var stream = memory.AsRandomAccessStream();
                using var image = await CanvasBitmap.LoadAsync(CanvasDevice.GetSharedDevice(), stream).AsTask(token);
                using var target = new CanvasRenderTarget(image.Device, 384, 216, 96);
                float slideWidth = presentation.SlideWidth > 0 ? (float)presentation.SlideWidth : (float)image.Size.Width;
                float slideHeight = presentation.SlideHeight > 0 ? (float)presentation.SlideHeight : (float)image.Size.Height;
                float scale = MathF.Min(384 / slideWidth, 216 / slideHeight);
                float width = slideWidth * scale, height = slideHeight * scale;
                float left = (384 - width) / 2, top = (216 - height) / 2;
                using (var drawing = target.CreateDrawingSession())
                {
                    drawing.Clear(Microsoft.UI.Colors.Transparent);
                    drawing.DrawImage(image, new Windows.Foundation.Rect(left, top, width, height));
                    using var clip = drawing.CreateLayer(1, new Windows.Foundation.Rect(left, top, width, height));
                    using var strokes = new StrokeRenderer();
                    var transform = ink.WorldToSlide * Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(left, top);
                    foreach (var stroke in ink.Strokes.OrderBy(stroke => stroke.LayerOrder ?? long.MaxValue))
                    {
                        token.ThrowIfCancellationRequested();
                        strokes.Draw(drawing, stroke, transform);
                    }
                }
                using var output = new InMemoryRandomAccessStream();
                await target.SaveAsync(output, CanvasBitmapFileFormat.Png).AsTask(token); output.Seek(0);
                var data = new byte[checked((int)output.Size)];
                using var reader = new DataReader(output);
                await reader.LoadAsync((uint)data.Length).AsTask(token); reader.ReadBytes(data);
                return data;
            }
            finally { CompositionGate.Release(); }
        }, token);
        token.ThrowIfCancellationRequested();
        using var encoded = new MemoryStream(pixels, writable: false);
        using var encodedStream = encoded.AsRandomAccessStream();
        var bitmap = new BitmapImage { DecodePixelWidth = 384 };
        await bitmap.SetSourceAsync(encodedStream).AsTask(token);
        return bitmap;
    }

    private IEnumerable<PptPageThumbnail> VisibleItems()
    {
        foreach (var pair in _containers)
        {
            if (pair.Key.ActualHeight <= 0) continue;
            DependencyObject parent = pair.Key;
            while (parent is not null && !ReferenceEquals(parent, ActiveView)) parent = VisualTreeHelper.GetParent(parent);
            if (parent is null) continue;
            var bounds = pair.Key.TransformToVisual(ActiveView).TransformBounds(new Windows.Foundation.Rect(0, 0, pair.Key.ActualWidth, pair.Key.ActualHeight));
            if (bounds.Bottom > 0 && bounds.Top < ActiveView.ActualHeight) yield return pair.Value;
        }
    }

    private void CancelViewport() => _viewportLoading?.Cancel();

    private void OnScrollChanged(object sender, ScrollViewerViewChangedEventArgs args)
    {
        CancelViewport();
        Schedule();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        SetOpen(false, null); _disposed = true;
        _context.Controller.Document.Changed -= InvalidateCurrent;
        _context.Controller.Invalidated -= OnControllerInvalidated;
        foreach (var scroll in _scrolls) scroll.ViewChanged -= OnScrollChanged;
        _scrolls.Clear(); _items.Clear(); _containers.Clear(); _list.ItemsSource = _grid.ItemsSource = null;
    }
}
