using InkCanvas.Core.Update;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace InkCanvas.App.Views;

public sealed partial class SettingsWindow
{
    private readonly List<Action> _repositoryRefreshers = new();
    private IReadOnlyList<ExtensionRelease> _displayedCatalog;
    private int _displayedPending;
    private TextBlock _repositoryStatus;

    private void RefreshRepositoryUpdates()
    {
        if (_closed) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            var updates = _services.ExtensionUpdates;
            if (_repositoryStatus is not null) _repositoryStatus.Text = updates.Status;
            foreach (var refresh in _repositoryRefreshers) refresh();
            RefreshUpdateStatus();
            if (_workspace?.SelectedPage == "模块与插件" && (!ReferenceEquals(_displayedCatalog, updates.Catalog) || _displayedPending != updates.Pending.Count))
                QueueRebuild();
        });
    }

    private void BuildRepositoryExtensions()
    {
        var updates = _services.ExtensionUpdates;
        if (!_indexingSearch)
        { _repositoryRefreshers.Clear(); _displayedCatalog = updates.Catalog; _displayedPending = updates.Pending.Count; }
        AddSection("仓库插件");
        _content.Children.Add(InfoRow("从项目 GitHub Release 下载并校验插件。安装和更新在后台准备，重启后生效，插件设置与数据继续保留。"));
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var check = new Button { Content = "检查插件更新", MinHeight = 40 };
        var all = new Button { Content = "一键更新全部", MinHeight = 40 };
        var restart = new Button { Content = "重启并应用", MinHeight = 40 };
        Accessible(check, "检查插件更新"); Accessible(all, "一键更新全部插件"); Accessible(restart, "重启并应用插件");
        check.Click += async (_, _) => await updates.CheckAsync();
        all.Click += async (_, _) => await updates.UpdateAllAsync();
        restart.Click += (_, _) => RestartApp();
        controls.Children.Add(check); controls.Children.Add(all); controls.Children.Add(restart);
        _content.Children.Add(Card(controls));
        void RefreshButtons()
        {
            check.IsEnabled = !updates.IsBusy;
            all.IsEnabled = !updates.IsBusy && updates.Catalog.Any(item => updates.HasUpdate(item) && updates.CanInstall(item));
            restart.Visibility = updates.Pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            restart.IsEnabled = !updates.IsBusy;
        }
        RefreshButtons(); if (!_indexingSearch) _repositoryRefreshers.Add(RefreshButtons);
        var status = new TextBlock { Text = updates.Status, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 6, 4, 8) };
        _content.Children.Add(status); if (!_indexingSearch) _repositoryStatus = status;
        foreach (var result in _services.ExtensionInstalls.Results.Where(result => !result.Success))
            _content.Children.Add(InfoRow("上次安装未完成 · " + result.Message));
        foreach (var release in updates.Catalog)
        {
            var action = new Button { MinHeight = 40, MinWidth = 100, HorizontalAlignment = HorizontalAlignment.Right };
            var description = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = SecondaryText() };
            var title = new TextBlock { Text = release.Name, FontSize = 15 };
            var text = new StackPanel { Spacing = 4, Children = { title, description } };
            var row = new Grid { ColumnSpacing = 16 };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(text); Grid.SetColumn(action, 1); row.Children.Add(action);
            _content.Children.Add(Searchable(Card(row), release.Name, "插件 下载 安装 更新 " + release.Id));
            Accessible(action, release.Name + "下载与更新");
            void Refresh()
            {
                string installed = updates.InstalledVersion(release);
                var pending = updates.Pending.FirstOrDefault(item => item.Id == release.Id);
                string reason = updates.UnavailableReason(release);
                description.Text = release.Description + "\n" + (installed is null ? "未安装" : "已安装 " + installed)
                    + " · 仓库 " + release.Version + (pending is null ? "" : "\n" + pending.Version + " 待重启生效")
                    + (reason is null ? "" : "\n" + reason);
                action.Content = updates.ActiveId == release.Id ? "正在准备" : updates.FailedInstall(release.Id) ? "重新下载" : pending is not null ? "待重启"
                    : installed is null ? "安装" : updates.HasUpdate(release) ? "更新" : "已是最新";
                action.IsEnabled = updates.CanInstall(release);
                ToolTipService.SetToolTip(action, reason ?? release.Asset.Name);
            }
            action.Click += async (_, _) =>
            {
                await updates.InstallAsync(release);
            };
            Refresh(); if (!_indexingSearch) _repositoryRefreshers.Add(Refresh);
        }
        if (!_indexingSearch && !updates.HasChecked && !updates.IsBusy) DispatcherQueue.TryEnqueue(async () =>
        { if (!_closed) await updates.CheckAsync(); });
    }
}
