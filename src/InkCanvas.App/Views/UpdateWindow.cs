using InkCanvas.App.Services;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Update;
using InkCanvas.Core.Versioning;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Views;

/// <summary>Native update confirmation, version selection and result notices.</summary>
internal sealed class UpdateWindow : Window
{
    private readonly TaskCompletionSource<ReleaseInfo> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IReadOnlyList<ReleaseInfo> _releases;
    private readonly AppVersion _current;
    private readonly ComboBox _versions = new() { Header = "选择版本", MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _version = new() { FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _size = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly MarkdownView _notes;
    private readonly InfoBar _information = new() { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational };
    private readonly Button _accept = new() { Content = "下载并更新", MinWidth = 132, MinHeight = 44 };
    private ReleaseInfo Selected => _versions.SelectedIndex >= 0 ? _releases[_versions.SelectedIndex] : null;
    public Task<ReleaseInfo> Result => _result.Task;

    public static UpdateWindow Confirm(ReleaseInfo release, AppVersion current, int theme)
        => new(new[] { release }, current, theme, chooseVersion: false);

    public static UpdateWindow Versions(IReadOnlyList<ReleaseInfo> releases, AppVersion current, int theme)
        => new(releases, current, theme, chooseVersion: true);

    public static UpdateWindow Notice(string title, string message, AppVersion current, int theme)
        => new(Array.Empty<ReleaseInfo>(), current, theme, chooseVersion: false, title, message);

    private UpdateWindow(IReadOnlyList<ReleaseInfo> releases, AppVersion current, int theme, bool chooseVersion,
        string noticeTitle = null, string noticeMessage = null)
    {
        _releases = releases.ToArray();
        _current = current;
        bool notice = noticeTitle is not null;
        bool dark = theme == 2;
        _notes = new MarkdownView(theme);
        Title = "软件更新 — Ink Canvas";
        var foreground = new SolidColorBrush(dark ? Microsoft.UI.Colors.White : Windows.UI.Color.FromArgb(255, 26, 26, 26));
        var secondary = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(255, 190, 190, 190) : Windows.UI.Color.FromArgb(255, 96, 96, 96));
        var surface = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 243, 243, 243));
        var root = new Grid { Background = surface };
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var content = new Grid { Padding = new Thickness(28, 20, 28, 24), RowSpacing = 20 };
        content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        content.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(content);

        var header = new Grid { ColumnSpacing = 16 };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(new FontIcon { Glyph = Glyphs.Download, FontSize = 32, Foreground = foreground,
            FontFamily = IconFontStyle.Font, Width = 44 });
        var titles = new StackPanel { Spacing = 6 };
        titles.Children.Add(new TextBlock { Text = noticeTitle ?? (chooseVersion ? "选择更新版本" : "发现新版本"), FontSize = 26,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = foreground, TextWrapping = TextWrapping.Wrap });
        titles.Children.Add(new TextBlock { Text = "当前版本 " + current, Foreground = secondary });
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        content.Children.Add(header);

        _versions.Visibility = chooseVersion ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_versions, "更新版本");
        foreach (var release in _releases) _versions.Items.Add(release.Tag + (release.Version == current ? " · 当前版本" : ""));
        _versions.SelectionChanged += (_, _) => RefreshRelease();
        Grid.SetRow(_versions, 1);
        content.Children.Add(_versions);

        var notesHeader = new Grid { ColumnSpacing = 12 };
        notesHeader.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        notesHeader.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _version.Foreground = foreground;
        _size.Foreground = secondary;
        notesHeader.Children.Add(_version);
        Grid.SetColumn(_size, 1);
        notesHeader.Children.Add(_size);
        var notesContent = new Grid { RowSpacing = 16 };
        notesContent.RowDefinitions.Add(new() { Height = GridLength.Auto });
        notesContent.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        notesContent.Children.Add(notesHeader);
        var notesScroll = new ScrollViewer { Content = _notes, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(notesScroll, "更新内容");
        Grid.SetRow(notesScroll, 1);
        notesContent.Children.Add(notesScroll);
        var card = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(20),
            BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(24, 255, 255, 255) : Windows.UI.Color.FromArgb(18, 0, 0, 0)),
            Background = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(255, 43, 43, 43) : Microsoft.UI.Colors.White), Child = notesContent };
        root.ActualThemeChanged += (_, _) => ApplyColors();
        Grid.SetRow(card, 2);
        content.Children.Add(card);
        _information.Visibility = notice ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(_information, 3);
        content.Children.Add(_information);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(28, 16, 28, 20) };
        var close = new Button { Content = notice ? "知道了" : chooseVersion ? "关闭" : "稍后", MinWidth = 112, MinHeight = 44 };
        AutomationProperties.SetName(close, close.Content.ToString());
        close.Click += (_, _) => Close();
        footer.Children.Add(close);
        _accept.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        AutomationProperties.SetName(_accept, "下载并更新");
        _accept.Visibility = notice ? Visibility.Collapsed : Visibility.Visible;
        _accept.Click += (_, _) => { if (Selected?.IsNewerThan(_current) == true) { _result.TrySetResult(Selected); Close(); } };
        footer.Children.Add(_accept);
        var footerBorder = new Border { BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = card.BorderBrush, Child = footer };
        Grid.SetRow(footerBorder, 1);
        root.Children.Add(footerBorder);
        root.KeyDown += (_, args) => { if (args.Key == Windows.System.VirtualKey.Escape) { args.Handled = true; Close(); } };
        Content = root;
        WindowChrome.MakeStandard(this, 640, notice ? 390 : 660);
        if (AppWindow.Presenter is OverlappedPresenter presenter) { presenter.IsMaximizable = false; presenter.IsMinimizable = false; }
        WindowFrame.Attach(this, root);
        WindowChrome.ApplyTheme(this, theme);
        ApplyColors();
        root.Loaded += (_, _) => (_accept.IsEnabled && !notice ? _accept : close).Focus(FocusState.Programmatic);
        Closed += (_, _) => _result.TrySetResult(null);
        if (notice)
        {
            notesHeader.Visibility = Visibility.Collapsed;
            _notes.Children.Add(new TextBlock { Text = noticeMessage, TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true, FontSize = 14, Foreground = foreground });
        }
        else
        {
            if (_releases.Count > 0) _versions.SelectedIndex = 0;
            else RefreshRelease();
        }

        void RefreshRelease()
        {
            var release = Selected;
            _version.Text = release?.Tag ?? "暂无可用版本";
            _size.Text = release?.InstallerSize > 0 ? $"下载大小 {release.InstallerSize / 1048576d:0.#} MB" : "";
            Uri.TryCreate(release?.PageUrl, UriKind.Absolute, out var pageUri);
            _notes.SetMarkdown(string.IsNullOrWhiteSpace(release?.Notes) ? "此版本暂无更新说明。" : release.Notes, pageUri);
            _accept.IsEnabled = release?.IsNewerThan(_current) == true;
            _information.Message = release is null ? "仓库没有适用的稳定版本。"
                : _accept.IsEnabled ? "下载并校验安装包后，将保存当前画布并重启完成更新。"
                : release.Version == _current ? "你正在使用此版本。" : "此版本早于当前版本。请选择较新的版本进行更新。";
        }

        void ApplyColors()
        {
            bool isDark = root.ActualTheme == ElementTheme.Dark;
            foreground.Color = isDark ? Microsoft.UI.Colors.White : Windows.UI.Color.FromArgb(255, 26, 26, 26);
            secondary.Color = isDark ? Windows.UI.Color.FromArgb(255, 190, 190, 190) : Windows.UI.Color.FromArgb(255, 96, 96, 96);
            surface.Color = isDark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 243, 243, 243);
            ((SolidColorBrush)card.BorderBrush).Color = isDark ? Windows.UI.Color.FromArgb(24, 255, 255, 255) : Windows.UI.Color.FromArgb(18, 0, 0, 0);
            ((SolidColorBrush)card.Background).Color = isDark ? Windows.UI.Color.FromArgb(255, 43, 43, 43) : Microsoft.UI.Colors.White;
        }
    }

}
