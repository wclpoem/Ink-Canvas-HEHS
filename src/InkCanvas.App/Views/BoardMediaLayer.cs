using System.Numerics;
using InkCanvas.Controls.Controls;
using InkCanvas.Features.Board;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace InkCanvas.App.Views;

/// <summary>Embedded video views retain their player while geometry and stacking change.</summary>
internal sealed class BoardMediaLayer : Canvas, IDisposable
{
    private sealed class Entry
    {
        public BoardMediaAsset Asset;
        public byte[] Data;
        public Grid View, Status;
        public MediaPlayerElement Element;
        public MediaPlayer Player;
        public MediaSource Source;
        public IRandomAccessStream Stream;
        public ProgressRing Progress;
        public TextBlock Message;
        public TouchIconButton Retry;
        public int Generation;
        public TypedEventHandler<MediaPlayer, object> Opened;
        public TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs> Failed;
    }

    private readonly Dictionary<Guid, Entry> _entries = new();
    private readonly Dictionary<byte[], Task<BitmapImage>> _posters = new();
    private BoardService _board;
    private Guid? _onlyAsset;
    private BoardMediaAsset _selected;
    private Matrix3x2 _viewTransform = Matrix3x2.Identity;
    private bool _disposed;
    public Canvas SharedHost { get; set; }
    public Func<Guid, int> LayerIndex { get; set; }
    private UIElementCollection ViewChildren => SharedHost?.Children ?? Children;
    public BoardMediaAsset SelectedMedia => _selected;
    public event Action SelectionChanged;
    internal int PlayerCount => _entries.Count;
    internal MediaPlayer GetPlayer(BoardMediaAsset asset) => _entries.GetValueOrDefault(asset.Id)?.Player;
    internal string GetStatus(BoardMediaAsset asset) => _entries.GetValueOrDefault(asset.Id)?.Message.Text;
    internal bool IsLoading(BoardMediaAsset asset) => _entries.GetValueOrDefault(asset.Id)?.Progress.IsActive == true;

    public BoardMediaLayer() => Background = null;
    public void SetSelection(IEnumerable<BoardMediaAsset> assets) => _selected = assets.FirstOrDefault();

    public void ClearSelection()
    {
        if (_selected is null) return;
        _selected = null;
        SelectionChanged?.Invoke();
    }

    public void SetPage(BoardService board, Guid? onlyAsset = null)
    {
        ClearEntries();
        _posters.Clear();
        _board = board;
        _onlyAsset = onlyAsset;
        _selected = null;
        Refresh();
    }

    public void UpdateViewTransform(Matrix3x2 transform)
    {
        if (_viewTransform == transform) return;
        _viewTransform = transform;
        if (SharedHost is not null)
        {
            foreach (var entry in _entries.Values) ApplyGeometry(entry);
        }
        else
        {
            if (RenderTransform is not MatrixTransform matrix) RenderTransform = matrix = new MatrixTransform();
            matrix.Matrix = new Matrix(transform.M11, transform.M12, transform.M21, transform.M22, transform.M31, transform.M32);
        }
    }

    public void Refresh()
    {
        if (_disposed) return;
        var assets = _board?.CurrentMedia ?? Array.Empty<BoardMediaAsset>();
        if (_onlyAsset is Guid onlyId) assets = assets.Where(asset => asset.Id == onlyId).ToArray();
        var present = assets.Select(asset => asset.Id).ToHashSet();
        foreach (var id in _entries.Keys.Where(id => !present.Contains(id)).ToArray())
        {
            var entry = _entries[id];
            _entries.Remove(id);
            DisposeEntry(entry);
        }
        var posters = assets.Select(asset => asset.PosterData).ToHashSet();
        foreach (var data in _posters.Keys.Where(data => !posters.Contains(data)).ToArray()) _posters.Remove(data);
        for (int index = 0; index < assets.Count; index++)
        {
            var asset = assets[index];
            if (_entries.TryGetValue(asset.Id, out var previous) && !ReferenceEquals(previous.Data, asset.Data))
            {
                _entries.Remove(asset.Id);
                DisposeEntry(previous);
            }
            if (!_entries.TryGetValue(asset.Id, out var entry)) entry = AddView(asset);
            entry.Asset = asset;
            ApplyGeometry(entry);
            SetZIndex(entry.View, LayerIndex?.Invoke(asset.Id) ?? index);
        }
        if (_selected is not null && !present.Contains(_selected.Id)) ClearSelection();
    }

    public void UpdateElement(BoardMediaAsset asset)
    {
        if (asset is not null && _entries.TryGetValue(asset.Id, out var entry)) ApplyGeometry(entry);
    }

    public UIElement GetViewElement(BoardMediaAsset asset) => _entries.GetValueOrDefault(asset.Id)?.View;

    private void ApplyGeometry(Entry entry)
    {
        var asset = entry.Asset;
        var view = entry.View;
        if (view.Width != asset.Width || view.Height != asset.Height)
        {
            view.Width = asset.Width;
            view.Height = asset.Height;
            view.Clip = new RectangleGeometry { Rect = new Rect(0, 0, asset.Width, asset.Height) };
        }
        SetLeft(view, asset.Left);
        SetTop(view, asset.Top);
        var combined = BoardElementMath.Read(asset.Transform) * (SharedHost is null ? Matrix3x2.Identity : _viewTransform);
        var origin = new Vector2(asset.Left, asset.Top);
        combined.Translation = Vector2.Transform(origin, combined) - origin;
        if (view.RenderTransform is not MatrixTransform matrix) view.RenderTransform = matrix = new MatrixTransform();
        matrix.Matrix = new Matrix(combined.M11, combined.M12, combined.M21, combined.M22, combined.M31, combined.M32);
    }

    private Entry AddView(BoardMediaAsset asset)
    {
        var entry = new Entry
        {
            Asset = asset,
            Data = asset.Data,
            Player = new MediaPlayer { AutoPlay = false, IsLoopingEnabled = false },
            Element = new MediaPlayerElement
            {
                AreTransportControlsEnabled = true,
                Stretch = Stretch.Uniform,
                MinWidth = 0,
                MinHeight = 0,
                TransportControls = new MediaTransportControls
                {
                    IsCompact = true,
                    IsZoomButtonVisible = false,
                    IsPlaybackRateButtonVisible = true,
                },
            },
            View = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Black), Tag = asset },
            Progress = new ProgressRing { Width = 28, Height = 28, IsActive = true, IsHitTestVisible = false },
            Message = new TextBlock { Text = "正在载入视频…", Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 280 },
            Retry = new TouchIconButton { Glyph = "\uF1A0", Label = "重试", Width = 48, Height = 48,
                ContentForeground = new SolidColorBrush(Microsoft.UI.Colors.White), Visibility = Visibility.Collapsed },
            Status = new Grid { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 0, 0, 0)) },
        };
        entry.Element.SetMediaPlayer(entry.Player);
        entry.View.Children.Add(entry.Element);
        var status = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        status.Children.Add(entry.Progress);
        status.Children.Add(entry.Message);
        status.Children.Add(entry.Retry);
        entry.Status.Children.Add(status);
        entry.View.Children.Add(entry.Status);
        entry.Retry.Click += (_, _) => OpenSource(entry);
        _entries.Add(asset.Id, entry);
        ViewChildren.Add(entry.View);
        OpenSource(entry);
        _ = SetPosterAsync(entry);
        return entry;
    }

    private void OpenSource(Entry entry)
    {
        CloseSource(entry);
        int generation = ++entry.Generation;
        entry.Status.Visibility = Visibility.Visible;
        entry.Progress.IsActive = true;
        entry.Progress.Visibility = Visibility.Visible;
        entry.Message.Text = "正在载入视频…";
        entry.Retry.Visibility = Visibility.Collapsed;
        entry.Opened = (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsCurrent(entry, generation)) return;
            entry.Progress.IsActive = false;
            entry.Status.Visibility = Visibility.Collapsed;
        });
        entry.Failed = (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (IsCurrent(entry, generation)) ShowFailure(entry);
        });
        entry.Player.MediaOpened += entry.Opened;
        entry.Player.MediaFailed += entry.Failed;
        try
        {
            entry.Stream = entry.Asset.OpenRead().AsRandomAccessStream();
            entry.Source = MediaSource.CreateFromStream(entry.Stream, entry.Asset.ContentType);
            entry.Player.Source = entry.Source;
        }
        catch { ShowFailure(entry); }
    }

    private void ShowFailure(Entry entry)
    {
        entry.Progress.IsActive = false;
        entry.Progress.Visibility = Visibility.Collapsed;
        entry.Message.Text = "无法播放此视频，可重试或换用其他视频文件。";
        entry.Retry.Visibility = Visibility.Visible;
        entry.Status.Visibility = Visibility.Visible;
    }

    private async Task SetPosterAsync(Entry entry)
    {
        if (entry.Asset.PosterData is not { Length: > 0 }) return;
        try
        {
            var data = entry.Asset.PosterData;
            if (!_posters.TryGetValue(data, out var loading)) _posters[data] = loading = LoadPosterAsync(data);
            var poster = await loading;
            if (!_disposed && _entries.GetValueOrDefault(entry.Asset.Id) == entry) entry.Element.PosterSource = poster;
        }
        catch { }
    }

    private static async Task<BitmapImage> LoadPosterAsync(byte[] data)
    {
        using var memory = new MemoryStream(data, writable: false);
        using var stream = memory.AsRandomAccessStream();
        var bitmap = new BitmapImage { DecodePixelWidth = 512 };
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    private bool IsCurrent(Entry entry, int generation) => !_disposed && entry.Generation == generation
        && _entries.GetValueOrDefault(entry.Asset.Id) == entry;

    private static void CloseSource(Entry entry)
    {
        if (entry.Opened is not null) entry.Player.MediaOpened -= entry.Opened;
        if (entry.Failed is not null) entry.Player.MediaFailed -= entry.Failed;
        entry.Player.Pause();
        entry.Player.Source = null;
        entry.Source?.Dispose();
        entry.Source = null;
        entry.Stream?.Dispose();
        entry.Stream = null;
    }

    private void DisposeEntry(Entry entry)
    {
        entry.Generation++;
        entry.Progress.IsActive = false;
        entry.Element.AreTransportControlsEnabled = false;
        entry.Element.SetMediaPlayer(null);
        CloseSource(entry);
        entry.Player.Dispose();
        ViewChildren.Remove(entry.View);
    }

    private void ClearEntries()
    {
        var entries = _entries.Values.ToArray();
        _entries.Clear();
        foreach (var entry in entries) DisposeEntry(entry);
    }

    public void PausePlayers()
    {
        foreach (var entry in _entries.Values) entry.Player.Pause();
    }

    public void ReleasePlayers()
    {
        ClearEntries();
        _posters.Clear();
    }

    internal static bool IsPlaybackControl(DependencyObject source)
    {
        bool control = false;
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            control |= current is ButtonBase or Slider or MediaTransportControls;
            if (current is FrameworkElement { Tag: BoardMediaAsset }) return control;
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearEntries();
        _posters.Clear();
    }
}
