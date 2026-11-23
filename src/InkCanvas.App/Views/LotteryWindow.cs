using System.ComponentModel;
using InkCanvas.App.Services;
using InkCanvas.Controls.Animation;
using InkCanvas.Controls.Controls;
using InkCanvas.Controls.Lifecycle;
using InkCanvas.Controls.Theming;
using InkCanvas.Features.Tools;
using InkCanvas.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace InkCanvas.App.Views;

/// <summary>Responsive result cards and large classroom touch controls.</summary>
public sealed partial class LotteryWindow : Window
{
    internal static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InkCanvas");
    private readonly LotteryService _lottery;
    private readonly bool _autoClose;
    private readonly string _dataDirectory;
    private readonly Action<LotteryWindow> _openRosterSettings;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _rollCancellation;
    private readonly MinSizeHook _minSize;
    private readonly Grid _root = new();
    private int _totalCount = 1;
    private bool _rolling, _closed, _syncing;
    private NamesInputWindow _namesInput;
    private IReadOnlyList<string> _lastResult = Array.Empty<string>();
    internal IReadOnlyList<string> LastResult => _lastResult;
    internal event Action OptionsChanged;
    /// <summary>抽取成功后触发，参数为本次抽中的姓名/编号（多人为顿号连接），供画板贴卡片。</summary>
    public event Action<string> Picked;
    private int DrawCount => Math.Min(_totalCount, _lottery.RemainingCount);

    public LotteryWindow(LotteryService lottery, bool autoClose = false, int theme = 0, string dataDirectory = null)
        : this(lottery, autoClose, theme, dataDirectory, null) { }

    public LotteryWindow(LotteryService lottery, bool autoClose, int theme, string dataDirectory,
        Action<LotteryWindow> openRosterSettings)
    {
        _lottery = lottery; _autoClose = autoClose;
        _dataDirectory = dataDirectory ?? DataDir; _openRosterSettings = openRosterSettings;
        Title = "抽奖 / 课堂点名 - Ink Canvas";
        BuildUi();
        Content = _root;
        WindowChrome.MakeStandard(this, 1100, 720);
        WindowFrame.Attach(this, _root).UseMica();
        WindowChrome.ApplyTheme(this, theme);
        _minSize = new MinSizeHook(this, 320, 360);
        _lottery.NamesChanged += OnNamesChanged;
        _lottery.ConfigurationChanged += OnConfigurationChanged;
        _root.ActualThemeChanged += (_, _) => ApplyPalette();
        _root.Loaded += async (_, _) =>
        {
            ApplyPalette(); ReloadNames();
            if (_autoClose)
            {
                try { await Task.Delay(100, _lifetime.Token); await RollAsync(); }
                catch (OperationCanceledException) { }
            }
        };
        Activated += (_, _) => { if (!_closed) ReloadNames(); };
        Closed += (_, _) =>
        {
            _closed = true; _lottery.NamesChanged -= OnNamesChanged;
            _lottery.ConfigurationChanged -= OnConfigurationChanged;
            _lifetime.Cancel(); _lifetime.Dispose();
            _minSize.Dispose(); _namesInput?.CloseWithoutSaving();
        };
        ApplyPalette(); RefreshSummary();
    }

    internal void ChangeCount(int value)
    {
        if (_rolling) return;
        _totalCount = Math.Clamp(value, 1, _lottery.PoolSize); RefreshSummary();
    }

    private void RefreshSummary()
    {
        _syncing = true;
        try
        {
            int remaining = _lottery.RemainingCount;
            _totalCount = Math.Clamp(_totalCount, 1, _lottery.PoolSize);
            RefreshStatusChips();
            _countLabel.Text = DrawCount.ToString();
            _poolInput.Value = _lottery.PeopleCount; _poolInput.IsEnabled = !_rolling && _lottery.Names.Count == 0;
            _samplingMode.SelectedIndex = (int)_lottery.SamplingMode;
            _cooldownPeriod.Value = _lottery.CooldownHalfLifeDays;
            _cooldownPeriod.IsEnabled = !_rolling && _lottery.SamplingMode == LotterySamplingMode.SoftCooldown;
            ToolTipService.SetToolTip(_cooldownPeriod, _lottery.SamplingMode == LotterySamplingMode.SoftCooldown
                ? "每经过设定天数，已有惩罚记忆减半。" : "软冷却点名模式下可调整。" );
            _rollButton.IsEnabled = !_rolling && remaining > 0;
            RefreshStartLabel();
            _minusButton.IsEnabled = !_rolling && DrawCount > 1;
            _plusButton.IsEnabled = !_rolling && DrawCount < remaining;
            _rosterChip.IsEnabled = _strategyChip.IsEnabled = _importButton.IsEnabled = _resetRound.IsEnabled = _samplingMode.IsEnabled = _optionsButton.IsEnabled = !_rolling;
            _resetRound.Visibility = _lottery.NoRepeat ? Visibility.Visible : Visibility.Collapsed;
            foreach (var (button, count) in _quickCounts)
            {
                button.IsEnabled = !_rolling && count <= remaining;
                button.IsSelected = count == DrawCount;
                button.SelectionBackground = PopupBrushes.BadgeBackground(_root.ActualTheme == ElementTheme.Dark ? 2 : 1);
                button.Background = count == DrawCount ? button.SelectionBackground
                    : PopupBrushes.Surface(_root.ActualTheme == ElementTheme.Dark ? 2 : 1);
                button.Foreground = count == DrawCount ? PopupBrushes.BadgeForeground(_root.ActualTheme == ElementTheme.Dark ? 2 : 1)
                    : PopupBrushes.Foreground(_root.ActualTheme == ElementTheme.Dark ? 2 : 1);
                button.RefreshVisual();
            }
            _copy.IsEnabled = !_rolling && _lastResult.Count > 0;
            _copy.Opacity = _copy.IsEnabled ? 1 : .4;
            _progress.IsActive = _rolling; _progress.Visibility = _rolling ? Visibility.Visible : Visibility.Collapsed;
            _history.Text = string.Join("\n", _lottery.History.TakeLast(5).Reverse().Select(draw =>
                $"{draw.Time.ToLocalTime():HH:mm}　{string.Join("、", draw.Names.Take(6))}" + (draw.Names.Count > 6 ? $" 等 {draw.Names.Count} 人" : "")));
        }
        finally { _syncing = false; }
    }

    private void ResetResults()
    {
        _lastResult = Array.Empty<string>(); _items.Clear(); _resultView.ItemsSource = null;
        _resultScroll.Visibility = _singleHost.Visibility = Visibility.Collapsed;
        _emptyHost.Visibility = Visibility.Visible;
        _singleName.Text = "";
        _resultTitle.Text = "抽选结果";
        _placeholder.Text = "准备抽选";
    }

    private void OnNamesChanged()
    {
        _rollCancellation?.Cancel(); ResetResults();
        _notice.Text = "名单已更新，新一轮抽选已准备好。"; RefreshSummary();
    }

    private void OnConfigurationChanged()
    {
        if (_rolling) { _rollCancellation?.Cancel(); ResetResults(); }
        _notice.Text = "";
        RefreshSummary();
    }

    private void ReloadNames()
    {
        try
        {
            if (File.Exists(Path.Combine(_dataDirectory, "Names.txt"))) _lottery.LoadNames(ClassroomRoster.Load(_dataDirectory));
        }
        catch { _notice.Text = "名单读取失败，已保留当前名单。"; }
        RefreshSummary();
    }

    private void OpenNamesInput()
    {
        if (_rolling || _closed) return;
        if (_openRosterSettings is not null) { _openRosterSettings(this); return; }
        if (_namesInput is not null) { _namesInput.Activate(); return; }
        var dialog = new NamesInputWindow(_root.ActualTheme == ElementTheme.Dark ? 2 : 1, _dataDirectory);
        _namesInput = dialog;
        dialog.Closed += (_, _) => { _namesInput = null; if (!_closed) ReloadNames(); };
        dialog.Activate(); WindowChrome.EnsureAbove(dialog);
    }

    private void ShowNames(IReadOnlyList<string> names)
    {
        if (_items.Count != names.Count)
        {
            _items = names.Select(name => new ResultItem { Name = name }).ToList();
            _resultView.ItemsSource = _items;
        }
        else for (int index = 0; index < names.Count; index++) _items[index].Name = names[index];
        bool single = names.Count == 1;
        _emptyHost.Visibility = Visibility.Collapsed;
        _singleHost.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
        _resultScroll.Visibility = single ? Visibility.Collapsed : Visibility.Visible;
        if (single) _singleName.Text = names[0];
        else { _measuredCount = -1; UpdateResultLayout(); }
    }

    internal async Task RollAsync()
    {
        if (_rolling || _closed || DrawCount == 0) return;
        _rolling = true; RefreshSummary();
        _resultScroll.ChangeView(null, 0, null, true);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _rollCancellation = cancellation;
        var token = cancellation.Token;
        int take = DrawCount;
        var used = _lottery.Used.ToHashSet();
        var candidates = Enumerable.Range(0, _lottery.PoolSize).Where(index => !_lottery.NoRepeat || !used.Contains(index))
            .Select(index => _lottery.Names.Count > 0 ? _lottery.Names[index] : (index + 1).ToString()).ToArray();
        var preview = new string[Math.Min(take, 24)];
        _resultTitle.Text = $"正在抽选 {take} 人"; _notice.Text = "";
        try
        {
            int previewFrames = Motion.AnimationsEnabled ? 14 : 0;
            for (int frame = 0; frame < previewFrames; frame++)
            {
                for (int index = 0; index < preview.Length; index++)
                {
                    int next = Random.Shared.Next(index, candidates.Length);
                    (candidates[index], candidates[next]) = (candidates[next], candidates[index]);
                    preview[index] = candidates[index];
                }
                ShowNames(preview);
                await Task.Delay(35 + frame * frame / 2, token);
            }
            token.ThrowIfCancellationRequested();
            _lastResult = _lottery.Draw(take); ShowNames(_lastResult);
            if (_lastResult.Count > 0) Picked?.Invoke(string.Join("、", _lastResult));
            _resultTitle.Text = $"本次抽选 {_lastResult.Count} 人";
            if (Motion.AnimationsEnabled) await Task.Yield();
            if (_closed || token.IsCancellationRequested) return;
            if (_lastResult.Count == 1) Motion.Pulse(_singleHost, 1.06f);
            else
            {
                UpdateResultLayout();
                for (int index = 0; index < Math.Min(_lastResult.Count, 12); index++)
                    if (_resultView.TryGetElement(index) is FrameworkElement card) Motion.PlayEntrance(card, 10, index * 24);
            }
            if (Motion.AnimationsEnabled) await Task.Delay(520, token);
            _notice.Text = _lottery.RemainingCount == 0 ? "本轮已全部抽完，重置本轮后可继续。" : "";
            if (_autoClose) { await Task.Delay(1800, token); if (!_closed) Close(); }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _rollCancellation = null; _rolling = false;
            if (!_closed) RefreshSummary();
        }
    }

    private void CopyResult()
    {
        if (_lastResult.Count == 0) return;
        try
        {
            var package = new DataPackage(); package.SetText(string.Join(Environment.NewLine, _lastResult));
            Clipboard.SetContent(package); _notice.Text = "已复制本次抽选结果。";
        }
        catch { _notice.Text = "暂时无法访问剪贴板，请重试。"; }
    }

    public sealed class ResultItem : INotifyPropertyChanged
    {
        private string _name;
        private double _fontSize = 36;
        private double _textWidth = 160;
        public string Name
        {
            get => _name;
            set
            {
                if (_name == value) return;
                _name = value;
                PropertyChanged?.Invoke(this, new(nameof(Name)));
            }
        }
        public double FontSize
        {
            get => _fontSize;
            set
            {
                if (_fontSize == value) return;
                _fontSize = value;
                PropertyChanged?.Invoke(this, new(nameof(FontSize)));
            }
        }
        public double TextWidth
        {
            get => _textWidth;
            set
            {
                if (_textWidth == value) return;
                _textWidth = value;
                PropertyChanged?.Invoke(this, new(nameof(TextWidth)));
            }
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }
}
