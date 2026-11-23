using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Numerics;
using InkCanvas.Controls.Controls;
using InkCanvas.Controls.Theming;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Board;
using InkCanvas.Ink;
using InkCanvas.Ink.Rendering;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace InkCanvas.App.Views;

public sealed class BoardPageThumbnail : INotifyPropertyChanged
{
    public InkController Controller { get; }
    public BoardPageThumbnail(InkController controller) => Controller = controller;
    private int _number;
    private ImageSource _preview;
    public int Number { get => _number; set { if (_number == value) return; _number = value; Notify(nameof(Title)); } }
    private string _name;
    public string Name { get => _name; set { if (_name == value) return; _name = value; Notify(nameof(Title)); } }
    public string Title => string.IsNullOrEmpty(Name) || Name == $"页面 {Number}" ? $"第 {Number} 页" : $"{Number}. {Name}";
    public ImageSource Preview { get => _preview; set { if (ReferenceEquals(_preview, value)) return; _preview = value; Notify(nameof(Preview)); } }
    public bool Dirty { get; set; } = true;
    public int Revision { get; set; }
    public int Theme { get; set; } = 1;
    public long LastUsed { get; set; }
    public event PropertyChangedEventHandler PropertyChanged;
    private void Notify(string property) => PropertyChanged?.Invoke(this, new(property));
}

/// <summary>Virtualized, touch-reorderable page list and grid sharing the board's live page identities.</summary>
internal sealed class BoardPageNavigator : UserControl, IDisposable
{
    internal const double SidebarWidth = 292;
    private readonly BoardService _board;
    private readonly Action<int> _navigate;
    private readonly Action _add, _duplicate, _delete, _rename;
    private readonly ObservableCollection<BoardPageThumbnail> _items = new();
    private readonly ListView _list = new();
    private readonly GridView _grid = new();
    private readonly Grid _root = new();
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly NumberBox _page = new() { Minimum = 1, SmallChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
        Width = 80, MinHeight = 44, IsWrapEnabled = false };
    private readonly PopupHeader _header;
    private readonly TouchIconButton _overviewButton, _undoPageButton, _redoPageButton;
    private readonly Dictionary<SelectorItem, BoardPageThumbnail> _containers = new();
    private readonly Dictionary<InkController, Action> _documentHandlers = new();
    private readonly HashSet<ScrollViewer> _scrollHosts = new();
    private readonly List<TouchIconButton> _buttons = new();
    private readonly DispatcherQueueTimer _refresh;
    private bool _syncing, _dragging, _disposed, _rendering, _open, _overview;
    private long _structure = -1, _clock;
    private int _generation;
    private bool _suspended;
    private readonly Func<Vector2> _canvasSize;
    public event Action CloseRequested;
    public event Action<bool> ViewModeChanged;
    public bool IsOverview => _overview;
    public int CachedThumbnailCount => _items.Count(item => item.Preview is not null);
    public int ItemCount => _items.Count;
    public ListViewBase ActiveView => _overview ? _grid : _list;

    public BoardPageNavigator(BoardService board, Func<Vector2> canvasSize, Action<int> navigate, Action add, Action duplicate, Action delete, Action rename)
    {
        _board = board; _canvasSize = canvasSize; _navigate = navigate; _add = add; _duplicate = duplicate; _delete = delete; _rename = rename;
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _header = new PopupHeader("页面管理", 1, canPin: false);
        _header.CloseRequested += () => CloseRequested?.Invoke();
        _root.Children.Add(_header);
        _overviewButton = Command("总览", Glyphs.Tools, () => SetOverview(!_overview));
        _undoPageButton = Command("撤销", Glyphs.Undo, () => _board.UndoPage());
        _redoPageButton = Command("重做", Glyphs.Redo, () => _board.RedoPage());
        ToolTipService.SetToolTip(_undoPageButton, "撤销页面操作");
        ToolTipService.SetToolTip(_redoPageButton, "重做页面操作");
        AutomationProperties.SetName(_undoPageButton, "撤销页面操作");
        AutomationProperties.SetName(_redoPageButton, "重做页面操作");
        var commands = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 8), Children =
        {
            Command("加页", Glyphs.Add, _add), Command("复制", "\uF32C", _duplicate),
            _overviewButton, _undoPageButton, _redoPageButton,
        } };
        Grid.SetRow(commands, 1); _root.Children.Add(commands);
        ConfigureView(_list, false); ConfigureView(_grid, true);
        Grid.SetRow(_list, 2); Grid.SetRow(_grid, 2);
        _grid.Visibility = Visibility.Collapsed;
        _root.Children.Add(_list); _root.Children.Add(_grid);
        var footer = new Grid { ColumnSpacing = 6, Margin = new Thickness(8, 8, 8, 12) };
        footer.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.Children.Add(_count); Grid.SetColumn(_page, 1); footer.Children.Add(_page);
        var go = Command("跳转", Glyphs.ChevronRight, JumpToPage); go.Width = go.Height = 44;
        Grid.SetColumn(go, 2); footer.Children.Add(go);
        AutomationProperties.SetName(_page, "跳转到页面");
        ToolTipService.SetToolTip(_page, "输入页码后按 Enter 或点击跳转");
        _page.AddHandler(UIElement.KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler((_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Enter) return;
            JumpToPage();
            args.Handled = true;
        }), true);
        Grid.SetRow(footer, 3); _root.Children.Add(footer);
        Content = _root;
        KeyDown += (_, args) =>
        {
            if (args.Handled || _dragging || KeyboardShortcutScope.IsTextInput(args.OriginalSource as DependencyObject)) return;
            bool modifiers = InkCanvas.Platform.Interop.NativeMethods.GetKeyState(0x11) < 0
                || InkCanvas.Platform.Interop.NativeMethods.GetKeyState(0x12) < 0 || InkCanvas.Platform.Interop.NativeMethods.GetKeyState(0x10) < 0;
            if (modifiers) return;
            if (args.Key == Windows.System.VirtualKey.Escape) { CloseRequested?.Invoke(); args.Handled = true; }
            else if (args.Key == Windows.System.VirtualKey.Delete) { _delete(); args.Handled = true; }
        };
        _refresh = DispatcherQueue.CreateTimer(); _refresh.Interval = TimeSpan.FromMilliseconds(220);
        _refresh.Tick += (_, _) => { _refresh.Stop(); _ = RenderVisibleAsync(); };
        _board.PageChanged += OnPageChanged;
        _board.BackgroundChanged += InvalidateCurrent;
        _board.ImagesChanged += InvalidateCurrent;
        _board.ImagePlacementChanged += OnImagePlacement;
        _board.MediaPlacementChanged += OnMediaPlacement;
        ApplyTheme(1);
    }

    private TouchIconButton Command(string label, string glyph, Action action)
    {
        var button = new TouchIconButton { Width = 52, Height = 52, Glyph = glyph, Label = label, IconSize = 20,
            IconAreaHeight = 26, LabelFontSize = 11, CornerRadius = new CornerRadius(5), Padding = new Thickness(2) };
        AutomationProperties.SetName(button, label); ToolTipService.SetToolTip(button, label);
        button.Click += (_, _) => { if (!_dragging) action(); };
        _buttons.Add(button); return button;
    }

    private void ConfigureView(ListViewBase view, bool grid)
    {
        view.ItemsSource = _items;
        view.CanDragItems = true; view.CanReorderItems = true; view.AllowDrop = true; view.IsSwipeEnabled = true;
        view.IsItemClickEnabled = true; view.SelectionMode = ListViewSelectionMode.Single;
        ScrollViewer.SetHorizontalScrollBarVisibility(view, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollMode(view, ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(view, ScrollBarVisibility.Auto);
        if (grid) view.ItemsPanel = (ItemsPanelTemplate)XamlReader.Load("""
            <ItemsPanelTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <ItemsWrapGrid Orientation="Horizontal" />
            </ItemsPanelTemplate>
            """);
        view.Loaded += (_, _) =>
        {
            var scroll = Descendants(view).OfType<ScrollViewer>().FirstOrDefault();
            if (scroll is not null && _scrollHosts.Add(scroll)) scroll.ViewChanged += OnScrollChanged;
            Schedule();
        };
        view.SizeChanged += (_, _) => Schedule();
        view.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <StackPanel Width="192" Spacing="6" Margin="4,8">
                    <Border Height="108" Background="{ThemeResource CardBackgroundFillColorDefaultBrush}" CornerRadius="4"
                            BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}" BorderThickness="1">
                        <Image Source="{Binding Preview}" Stretch="Uniform" />
                    </Border>
                    <TextBlock Text="{Binding Title}" HorizontalAlignment="Center" FontSize="13" />
                </StackPanel>
            </DataTemplate>
            """);
        view.ItemContainerStyle = new Style(grid ? typeof(GridViewItem) : typeof(ListViewItem));
        view.ItemContainerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        view.ItemContainerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(2)));
        view.ItemContainerStyle.Setters.Add(new Setter(Control.MinHeightProperty, 44d));
        view.ItemClick += (_, args) =>
        {
            if (_syncing || _dragging || args.ClickedItem is not BoardPageThumbnail item) return;
            int index = FindPage(item.Controller);
            if (index >= 0)
            {
                _navigate(index);
                if (_overview) CloseRequested?.Invoke();
            }
        };
        view.SelectionChanged += (_, _) =>
        {
            if (_syncing || _dragging || view.SelectedItem is not BoardPageThumbnail item) return;
            int index = FindPage(item.Controller);
            if (index >= 0) _navigate(index);
        };
        view.ContainerContentChanging += (_, args) =>
        {
            if (args.Item is not BoardPageThumbnail item) return;
            if (args.InRecycleQueue) _containers.Remove(args.ItemContainer);
            else
            {
                _containers[args.ItemContainer] = item;
                args.ItemContainer.ContextFlyout ??= CreatePageMenu(args.ItemContainer);
                item.LastUsed = ++_clock;
                Schedule();
            }
        };
        view.DragItemsStarting += (_, args) =>
        {
            if (_board.Current.HasActiveInteraction) { args.Cancel = true; return; }
            args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
            _dragging = true;
        };
        view.DragItemsCompleted += (_, args) =>
        {
            CompleteReorder(_open && args.DropResult == Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move
                ? args.Items.FirstOrDefault() as BoardPageThumbnail : null);
        };
    }

    private MenuFlyout CreatePageMenu(SelectorItem container)
    {
        var menu = new MenuFlyout();
        Add("重命名", _rename);
        Add("复制页面", _duplicate);
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("删除", _delete);
        return menu;

        void Add(string text, Action action)
        {
            var command = new MenuFlyoutItem { Text = text };
            command.Click += (_, _) =>
            {
                if (_board.Current.HasActiveInteraction || !_containers.TryGetValue(container, out var item)) return;
                int index = FindPage(item.Controller);
                if (index < 0) return;
                _navigate(index);
                if (ReferenceEquals(_board.Current, item.Controller)) action();
            };
            menu.Items.Add(command);
        }
    }

    internal bool CompleteReorder(BoardPageThumbnail item)
    {
        _dragging = true;
        try
        {
            if (item is null || _board.Current.HasActiveInteraction) return false;
            int source = FindPage(item.Controller), target = _items.IndexOf(item);
            return source >= 0 && target >= 0 && _board.MovePage(source, target);
        }
        finally { _dragging = false; Synchronize(force: true); }
    }

    public void SetOpen(bool open)
    {
        _open = open; _generation++;
        if (open)
        {
            foreach (var item in _items) { item.Dirty = true; item.Revision++; }
            Synchronize(); Schedule();
        }
        else { _refresh.Stop(); foreach (var item in _items) item.Preview = null; }
    }

    public void SetSuspended(bool suspended)
    {
        _suspended = suspended;
        if (suspended) { _generation++; _refresh.Stop(); foreach (var item in _items) item.Preview = null; }
        else if (_open) Schedule();
    }

    public void SetOverview(bool overview)
    {
        if (_overview == overview) return;
        _overview = overview; _generation++;
        _list.Visibility = overview ? Visibility.Collapsed : Visibility.Visible;
        _grid.Visibility = overview ? Visibility.Visible : Visibility.Collapsed;
        _overviewButton.IsSelected = overview;
        _overviewButton.Label = overview ? "返回列表" : "总览";
        _overviewButton.Glyph = overview ? Glyphs.SlideList : Glyphs.Tools;
        AutomationProperties.SetName(_overviewButton, _overviewButton.Label);
        ToolTipService.SetToolTip(_overviewButton, _overviewButton.Label);
        ViewModeChanged?.Invoke(overview);
        if (_open) { Synchronize(); Schedule(); }
    }

    private void OnPageChanged()
    {
        if (_dragging || !_open && _items.Count == 0) return;
        Synchronize(); Schedule();
    }

    private void Synchronize(bool force = false)
    {
        if (_disposed || _syncing || _dragging) return;
        _syncing = true;
        try
        {
            if (force || _structure != _board.PageStructureVersion)
            {
                _generation++;
                var existing = _items.ToDictionary(item => item.Controller);
                var live = _board.Pages.ToHashSet();
                foreach (var item in _items.Where(item => !live.Contains(item.Controller)).ToArray())
                {
                    if (_documentHandlers.Remove(item.Controller, out var handler)) item.Controller.Document.Changed -= handler;
                    foreach (var container in _containers.Where(pair => ReferenceEquals(pair.Value, item)).Select(pair => pair.Key).ToArray()) _containers.Remove(container);
                    item.Preview = null;
                }
                _items.Clear();
                for (int index = 0; index < _board.PageCount; index++)
                {
                    var controller = _board.Pages[index];
                    if (!existing.TryGetValue(controller, out var item))
                    {
                        item = new(controller);
                        var observed = item;
                        Action handler = () => { observed.Dirty = true; observed.Revision++; Schedule(); };
                        _documentHandlers.Add(controller, handler); controller.Document.Changed += handler;
                    }
                    item.Number = index + 1; item.Name = _board.PageName(index); _items.Add(item);
                }
                _structure = _board.PageStructureVersion;
            }
            _list.SelectedItem = _grid.SelectedItem = _items.FirstOrDefault(item => ReferenceEquals(item.Controller, _board.Current));
            _count.Text = $"共 {_board.PageCount} 页";
            _page.Maximum = _board.PageCount; _page.Value = _board.CurrentIndex + 1;
            foreach (var button in _buttons.Where(button => button.Label is "加页" or "复制")) button.IsEnabled = _board.CanAddPages();
            _undoPageButton.IsEnabled = _board.CanUndoPage;
            _redoPageButton.IsEnabled = _board.CanRedoPage;
            if (ActiveView.SelectedItem is not null) ActiveView.ScrollIntoView(ActiveView.SelectedItem);
        }
        finally { _syncing = false; }
    }

    private void OnImagePlacement(BoardImageAsset asset) => InvalidateCurrent();
    private void OnMediaPlacement(BoardMediaAsset asset) => InvalidateCurrent();
    private void JumpToPage()
    {
        if (double.IsFinite(_page.Value)) _navigate(Math.Clamp((int)_page.Value - 1, 0, _board.PageCount - 1));
    }
    public void InvalidateCurrent()
    {
        var item = _items.FirstOrDefault(item => ReferenceEquals(item.Controller, _board.Current));
        if (item is null) return;
        item.Dirty = true; item.Revision++; Schedule();
    }
    public void InvalidateAll()
    {
        foreach (var item in _items) { item.Dirty = true; item.Revision++; }
        _generation++; Schedule();
    }
    private void Schedule()
    {
        if (!_disposed && _open && !_suspended) { _refresh.Stop(); _refresh.Start(); }
    }

    public void ApplyTheme(int theme)
    {
        RequestedTheme = theme == 2 ? ElementTheme.Dark : ElementTheme.Light;
        _root.Background = null;
        _header.ApplyTheme(theme); _count.Foreground = PopupBrushes.Foreground(theme);
        foreach (var button in _buttons)
        {
            button.ContentForeground = PopupBrushes.FloatBarForeground(theme);
            button.HoverBackground = PopupBrushes.FloatBarButtonHover(theme);
            button.PressedBackground = PopupBrushes.FloatBarButtonPressed(theme);
            button.SelectionBackground = PopupBrushes.FloatBarSelected(); button.RefreshVisual();
        }
    }

    private async Task RenderVisibleAsync()
    {
        if (_disposed || !_open || _suspended || _rendering) return;
        if (_board.Current.HasActiveInteraction) { Schedule(); return; }
        _rendering = true;
        int generation = _generation;
        try
        {
            foreach (var item in VisibleItems().OrderBy(item => Math.Abs(item.Number - _board.CurrentIndex - 1)).ToArray())
            {
                if (_disposed || !_open || _suspended || generation != _generation || _board.Current.HasActiveInteraction) break;
                if (!item.Dirty && item.Preview is not null) continue;
                int index = FindPage(item.Controller);
                if (index < 0) continue;
                int revision = item.Revision;
                var info = _board.GetPageInfo(index);
                var objects = info.Controller.DrawingObjects.ToArray();
                var posters = objects.Select(entry => entry.Element).OfType<BoardSelectionElement>()
                    .Where(element => element.Media?.PosterData is { Length: > 0 }).ToDictionary(element => element.Id, element =>
                    {
                        var media = element.Media;
                        var image = new BoardImageAsset { Data = media.PosterData, Left = media.Left, Top = media.Top, Width = media.Width,
                            Height = media.Height, Transform = media.Transform?.ToArray(), ContentType = "image/png" };
                        return new BoardSelectionElement(_board, image, info.Controller);
                    });
                var viewport = _canvasSize();
                using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 192, 108, 96);
                using var scene = new InkSceneRenderer(384);
                await scene.Images.PrepareAsync(target.Device, ImageSceneRenderer.Images(info.Controller).Concat(posters.Values));
                if (_disposed || !_open || generation != _generation || _board.Current.HasActiveInteraction) break;
                if (item.Revision != revision) continue;
                var view = Fit(viewport, info.Controller.ViewTransform);
                using (var drawing = target.CreateDrawingSession())
                {
                    DrawPaper(drawing, info, viewport);
                    foreach (var entry in objects)
                    {
                        if (entry.Element is IImageSelectionElement { ImageData.Length: > 0 } image) scene.Images.Draw(drawing, image, view);
                        if (entry.Stroke is not null)
                        {
                            scene.Strokes.Draw(drawing, entry.Stroke, view);
                        }
                        if (entry.Element is BoardSelectionElement { Media: { } media })
                        {
                            var previous = drawing.Transform;
                            drawing.Transform = BoardElementMath.Read(media.Transform) * view;
                            if (posters.TryGetValue(media.Id, out var poster)) scene.Images.Draw(drawing, poster, view);
                            else drawing.FillRectangle(media.Left, media.Top, media.Width, media.Height, Windows.UI.Color.FromArgb(255, 48, 52, 60));
                            drawing.DrawLine(new(media.Left + media.Width * .4f, media.Top + media.Height * .3f),
                                new(media.Left + media.Width * .65f, media.Top + media.Height * .5f), Microsoft.UI.Colors.White, 4);
                            drawing.DrawLine(new(media.Left + media.Width * .65f, media.Top + media.Height * .5f),
                                new(media.Left + media.Width * .4f, media.Top + media.Height * .7f), Microsoft.UI.Colors.White, 4);
                            drawing.DrawLine(new(media.Left + media.Width * .4f, media.Top + media.Height * .7f),
                                new(media.Left + media.Width * .4f, media.Top + media.Height * .3f), Microsoft.UI.Colors.White, 4);
                            drawing.Transform = previous;
                        }
                    }
                }
                using var stream = new InMemoryRandomAccessStream();
                await target.SaveAsync(stream, CanvasBitmapFileFormat.Png); stream.Seek(0);
                var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream);
                if (_disposed || !_open || generation != _generation || item.Revision != revision || !VisibleItems().Contains(item)) continue;
                item.Preview = bitmap; item.Dirty = false; item.LastUsed = ++_clock;
                int cacheLimit = Math.Max(24, VisibleItems().Count());
                foreach (var expired in _items.Where(value => value.Preview is not null).OrderByDescending(value => value.LastUsed).Skip(cacheLimit)) expired.Preview = null;
                await Task.Delay(8);
            }
        }
        catch (Exception ex) { if (!_disposed) Core.Diagnostics.FileLog.WriteLogToFile("Board thumbnail: " + ex.Message); }
        finally
        {
            _rendering = false;
            if (!_disposed && _open && !_suspended && VisibleItems().Any(item => item.Dirty || item.Preview is null)) Schedule();
        }
    }

    private int FindPage(InkController controller)
    {
        for (int i = 0; i < _board.PageCount; i++) if (ReferenceEquals(_board.Pages[i], controller)) return i;
        return -1;
    }

    private void OnScrollChanged(object sender, ScrollViewerViewChangedEventArgs args) => Schedule();

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private IEnumerable<BoardPageThumbnail> VisibleItems()
    {
        var seen = new HashSet<BoardPageThumbnail>();
        foreach (var pair in _containers)
        {
            DependencyObject parent = pair.Key;
            while (parent is not null && !ReferenceEquals(parent, ActiveView)) parent = VisualTreeHelper.GetParent(parent);
            if (parent is null || pair.Key.ActualHeight <= 0) continue;
            var bounds = pair.Key.TransformToVisual(ActiveView).TransformBounds(new Windows.Foundation.Rect(0, 0, pair.Key.ActualWidth, pair.Key.ActualHeight));
            if (bounds.Bottom > 0 && bounds.Top < ActiveView.ActualHeight && seen.Add(pair.Value)) yield return pair.Value;
        }
    }

    private static Matrix3x2 Fit(Vector2 viewport, Matrix3x2 view)
    {
        float scale = MathF.Min(192 / MathF.Max(1, viewport.X), 108 / MathF.Max(1, viewport.Y));
        return view * Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation((192 - viewport.X * scale) / 2, (108 - viewport.Y * scale) / 2);
    }

    private static void DrawPaper(CanvasDrawingSession drawing, BoardPageInfo info, Vector2 viewport)
    {
        drawing.Clear(info.Background switch
        {
            BoardBackground.Black => Windows.UI.Color.FromArgb(255, 31, 31, 31),
            BoardBackground.Green => Windows.UI.Color.FromArgb(255, 0x1E, 0x3B, 0x2F),
            BoardBackground.Cream => Windows.UI.Color.FromArgb(255, 0xF5, 0xEF, 0xDC),
            _ => Windows.UI.Color.FromArgb(255, 242, 242, 242),
        });
        if (info.Background is not (BoardBackground.Grid or BoardBackground.Lined)) return;
        var transform = Fit(viewport, info.Controller.ViewTransform);
        if (!Matrix3x2.Invert(info.Controller.ViewTransform, out var inverse)) return;
        var low = Vector2.Transform(Vector2.Zero, inverse);
        var high = Vector2.Transform(viewport, inverse);
        float spacing = info.GridSpacing;
        drawing.Transform = transform;
        var color = Windows.UI.Color.FromArgb(65, 92, 113, 121);
        float width = .6f / MathF.Max(.001f, MathF.Sqrt(MathF.Abs(transform.GetDeterminant())));
        if (info.Background == BoardBackground.Grid)
            for (float x = MathF.Floor(low.X / spacing) * spacing; x <= high.X; x += spacing) drawing.DrawLine(x, low.Y, x, high.Y, color, width);
        for (float y = MathF.Floor(low.Y / spacing) * spacing; y <= high.Y; y += spacing) drawing.DrawLine(low.X, y, high.X, y, color, width);
        drawing.Transform = Matrix3x2.Identity;
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _generation++; _refresh.Stop();
        foreach (var scroll in _scrollHosts) scroll.ViewChanged -= OnScrollChanged;
        _scrollHosts.Clear();
        _board.PageChanged -= OnPageChanged; _board.BackgroundChanged -= InvalidateCurrent; _board.ImagesChanged -= InvalidateCurrent;
        _board.ImagePlacementChanged -= OnImagePlacement; _board.MediaPlacementChanged -= OnMediaPlacement;
        foreach (var pair in _documentHandlers) pair.Key.Document.Changed -= pair.Value;
        _documentHandlers.Clear();
        foreach (var item in _items) item.Preview = null;
        _items.Clear(); _containers.Clear(); _list.ItemsSource = _grid.ItemsSource = null;
    }
}
