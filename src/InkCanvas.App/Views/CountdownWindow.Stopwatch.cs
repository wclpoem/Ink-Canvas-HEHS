using System.Collections.ObjectModel;
using InkCanvas.Controls.Controls;
using InkCanvas.Features.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace InkCanvas.App.Views;

public sealed partial class CountdownWindow
{
    private readonly SegmentedSelector _timerModes = new(new[] { "倒计时", "秒表" }) { Width = 180 };
    private readonly Grid _modeHost = new(), _compactModeHost = new(), _headerModeHost = new();
    private readonly ObservableCollection<LapRow> _lapItems = new();
    private readonly StackPanel _lapPanel = new() { Spacing = 4, Visibility = Visibility.Collapsed };
    private readonly ListView _lapList = new() { MaxHeight = 112, SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = false };
    private bool _stopwatchMode;
    private int _displayFraction = -1;
    private sealed record LapRow(int Number, string Split, string Elapsed);

    private void ConfigureStopwatch()
    {
        for (int index = 0; index < 2; index++)
        {
            var glyph = index == 0 ? Glyphs.TimerWpf : Glyphs.Stopwatch;
            _timerModes.SetOptionContent(index, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Center, Children = { Icon(glyph), new TextBlock { Text = index == 0 ? "倒计时" : "秒表", VerticalAlignment = VerticalAlignment.Center } } }, 40);
        }
        _timerModes.SelectedIndex = 0;
        _timerModes.SelectionChanged += () => SwitchTimerMode(_timerModes.SelectedIndex == 1);
        _headerModeHost.Children.Add(_timerModes); _frame.Header.Content = _headerModeHost;
        _lapList.ItemsSource = _lapItems;
        _lapList.Padding = new Thickness(0);
        _lapList.ItemContainerStyle = new Style(typeof(ListViewItem)) { Setters = {
            new Setter(Control.PaddingProperty, new Thickness(0)),
            new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
            new Setter(FrameworkElement.MinHeightProperty, 36d) } };
        _lapList.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <Grid MinHeight="36" Padding="12,4" ColumnSpacing="12">
                <Grid.ColumnDefinitions><ColumnDefinition Width="64"/><ColumnDefinition Width="*"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                <TextBlock Text="{Binding Number}" VerticalAlignment="Center" FontSize="13"/>
                <TextBlock Grid.Column="1" Text="{Binding Split}" TextAlignment="Right" VerticalAlignment="Center" FontSize="13" Typography.NumeralAlignment="Tabular"/>
                <TextBlock Grid.Column="2" Text="{Binding Elapsed}" TextAlignment="Right" VerticalAlignment="Center" FontSize="13" Typography.NumeralAlignment="Tabular"/>
              </Grid>
            </DataTemplate>
            """);
        var heading = new Grid { ColumnSpacing = 12, Padding = new Thickness(12, 4, 12, 4), Opacity = .65 };
        heading.ColumnDefinitions.Add(new() { Width = new GridLength(64) });
        heading.ColumnDefinitions.Add(new()); heading.ColumnDefinitions.Add(new());
        foreach (var item in new[] { ("计次", 0), ("本次用时", 1), ("累计用时", 2) })
        {
            var text = new TextBlock { Text = item.Item1, FontSize = 12, TextAlignment = item.Item2 == 0 ? TextAlignment.Left : TextAlignment.Right };
            Grid.SetColumn(text, item.Item2); heading.Children.Add(text);
        }
        _lapPanel.Children.Add(heading); _lapPanel.Children.Add(_lapList);
        AutomationProperties.SetName(_timerModes, "计时模式"); AutomationProperties.SetName(_lapList, "秒表计次记录");
    }

    internal void SwitchTimerMode(bool stopwatch)
    {
        if (_stopwatchMode == stopwatch || _disposed) return;
        _durationFlyout.Hide();
        _stopwatchMode = stopwatch;
        _timerModes.SelectedIndex = stopwatch ? 1 : 0;
        _displaySeconds = _displayFraction = -1;
        SyncRefreshTimer(); RefreshState();
    }

    private void PlaceTimerModes()
    {
        var target = _mode == TimerView.Compact ? _compactModeHost : _mode == TimerView.FullScreen || _root.ActualWidth < 760 ? _modeHost : _headerModeHost;
        if (target.Children.Contains(_timerModes)) return;
        foreach (var host in new[] { _modeHost, _compactModeHost, _headerModeHost }) host.Children.Remove(_timerModes);
        _modeHost.Visibility = target == _modeHost ? Visibility.Visible : Visibility.Collapsed;
        _headerModeHost.Visibility = target == _headerModeHost ? Visibility.Visible : Visibility.Collapsed;
        target.Children.Add(_timerModes);
    }

    private void AddTimeOrLap()
    {
        if (!_stopwatchMode) { AddTime(60); return; }
        RecordLap();
    }

    internal void RecordLap()
    {
        var lap = _stopwatch.Lap();
        if (lap is null) return;
        _lapItems.Insert(0, new(lap.Number, LapTime(lap.Split), LapTime(lap.Elapsed)));
        _lapPanel.Visibility = _stopwatchMode ? Visibility.Visible : Visibility.Collapsed;
        _endTime.Text = $"已记录 {_lapItems.Count} 次";
        _lapList.ScrollIntoView(_lapItems[0]);
    }

    private void RefreshStopwatchTime()
    {
        var elapsed = _stopwatch.Elapsed;
        int seconds = (int)elapsed.TotalSeconds;
        if (seconds != _displaySeconds)
        {
            _displaySeconds = seconds;
            _time.Text = $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}";
        }
        int fraction = elapsed.Milliseconds / 10;
        if (fraction != _displayFraction)
        {
            _displayFraction = fraction;
            _faceHint.Text = $".{fraction:00}";
        }
        _compactTime.Text = _mode == TimerView.Compact ? _time.Text + _faceHint.Text : _time.Text;
        _ring.Value = elapsed.TotalSeconds % 60 / 60;
        _ring.IsPaused = !_stopwatch.IsRunning;
        if (_warning) { _warning = false; ApplyPalette(); }
    }

    private static string LapTime(TimeSpan elapsed) => $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}.{elapsed.Milliseconds / 10:00}";
}
