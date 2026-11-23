using InkCanvas.Controls.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.Modules.AiAssistant.Providers;

public sealed class AiConnectionSettingsView : StackPanel
{
    private readonly AiConnectionStore _store;
    private readonly ComboBox _connections = new() { Header = "已保存连接", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _preset = new() { Header = "供应商预设", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _format = new() { Header = "API 格式", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _name = new() { Header = "连接名称" };
    private readonly TextBox _address = new() { Header = "API 基础地址或完整接口地址", PlaceholderText = "https://gateway.example/v1" };
    private readonly TextBox _modelsAddress = new() { Header = "模型列表地址（可选）", PlaceholderText = "留空则由 API 地址推导 /models" };
    private readonly PasswordBox _key = new() { Header = "API Key", PasswordRevealMode = PasswordRevealMode.Peek };
    private readonly ComboBox _model = new() { Header = "模型", IsEditable = true, HorizontalAlignment = HorizontalAlignment.Stretch,
        PlaceholderText = "获取列表后选择，也可手动输入模型 ID" };
    private readonly ToggleSwitch _images = new() { Header = "模型支持图片输入", OnContent = "发送图片附件", OffContent = "回退为文字" };
    private readonly ToggleSwitch _files = new() { Header = "模型支持 PDF 文件输入", OnContent = "发送 PDF 原文", OffContent = "仅提取可读文字" };
    private readonly ComboBox _reasoning = new() { Header = "模型思考参数支持", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ToggleSwitch _search = new() { Header = "联网搜索", OnContent = "启用", OffContent = "关闭" };
    private readonly NumberBox _maxTokens = new() { Header = "最大输出 Token", Minimum = 256, Maximum = 131072,
        Value = 8192, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly InfoBar _status = new() { IsClosable = false };
    private readonly Button _fetch = new() { Content = "获取模型列表", MinHeight = 44 };
    private readonly Button _test = new() { Content = "测试连接", MinHeight = 44 };
    private readonly Button _save = new() { Content = "保存并使用", MinHeight = 44 };
    private readonly Button _delete = new() { Content = "删除连接", MinHeight = 44 };
    private readonly TextBlock _active = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    private CancellationTokenSource _request;
    private AiConnection _editing;
    private bool _loading;
    private int _generation;

    public AiConnectionSettingsView(AiConnectionStore store)
    {
        _store = store; Spacing = 12;
        foreach (var provider in AiProviderCatalog.Presets)
            _preset.Items.Add(new ComboBoxItem { Tag = provider.Id, Content = ProviderIcon.Label(provider.Icon, provider.Name) });
        foreach (var format in Enum.GetValues<AiApiFormat>()) _format.Items.Add(AiProviderCatalog.FormatName(format));
        foreach (string label in new[] { "自动识别", "支持", "不支持" }) _reasoning.Items.Add(label);
        var newConnection = new Button { Content = "添加连接", MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
        newConnection.Click += (_, _) => Load(Preset().Create());
        _preset.SelectedIndex = 0;
        Children.Add(_active); Children.Add(_connections); Children.Add(_preset); Children.Add(newConnection);
        foreach (var control in new FrameworkElement[] { _name, _format, _address, _modelsAddress, _key, _model, _maxTokens, _images, _files, _reasoning, _search })
        {
            control.MinHeight = 44;
            Children.Add(control);
        }
        var actions = new Grid { ColumnSpacing = 8 };
        actions.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        actions.Children.Add(_fetch); actions.Children.Add(_test); Grid.SetColumn(_test, 1); Children.Add(actions);
        _save.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        _save.HorizontalAlignment = HorizontalAlignment.Stretch;
        _delete.HorizontalAlignment = HorizontalAlignment.Stretch;
        Children.Add(_save); Children.Add(_delete);
        Children.Add(_status);
        _save.Click += (_, _) => Execute(() => { _store.Save(Read(), _key.Password); Refresh(); Load(_store.Active); Report("连接已保存，新请求将使用所选模型。", false); });
        _delete.Click += async (_, _) =>
        {
            if (_editing is null || !_store.State.Connections.Any(connection => connection.Id == _editing.Id)) return;
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "删除 AI 连接", Content = "删除此连接及其 API Key？",
                PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            Execute(() => { _store.Delete(_editing.Id); Refresh(); Load(Preset().Create()); Report("连接已删除。", false); });
        };
        _fetch.Click += async (_, _) => await RequestAsync(fetch: true);
        _test.Click += async (_, _) => await RequestAsync(fetch: false);
        _connections.SelectionChanged += (_, _) =>
        {
            if (_loading || _connections.SelectedItem is not ComboBoxItem { Tag: string id }) return;
            Load(_store.State.Connections.Single(connection => connection.Id == id));
        };
        _format.SelectionChanged += (_, _) => UpdateSearch();
        Unloaded += (_, _) => { _generation++; _request?.Cancel(); SetBusy(false); };
        _model.SelectionChanged += (_, _) =>
        { if (_model.SelectedItem is ComboBoxItem { Tag: string id }) _model.Text = id; };
        SetNames(); Refresh();
        Load(_store.Active ?? _store.State.Connections.FirstOrDefault() ?? Preset().Create());
    }

    private AiProviderPreset Preset() => AiProviderCatalog.Presets.Single(provider => provider.Id == (string)((ComboBoxItem)_preset.SelectedItem).Tag);
    private void Load(AiConnection connection)
    {
        _generation++; _request?.Cancel(); SetBusy(false); _loading = true;
        try
        {
            _editing = connection;
            _name.Text = connection.Name; _address.Text = connection.BaseUrl; _modelsAddress.Text = connection.ModelsUrl;
            _format.SelectedIndex = (int)connection.Format;
            _model.Items.Clear();
            if (!string.IsNullOrWhiteSpace(connection.Model))
            {
                var savedModel = new ComboBoxItem { Tag = connection.Model, Content = connection.Model };
                _model.Items.Add(savedModel); _model.SelectedItem = savedModel;
            }
            _model.Text = connection.Model;
            _maxTokens.Value = connection.MaxOutputTokens; _images.IsOn = connection.SendImages;
            _files.IsOn = connection.SendFiles;
            _reasoning.SelectedIndex = connection.Reasoning switch { "supported" => 1, "unsupported" => 2, _ => 0 };
            _key.Password = _store.Key(connection.Id);
            _search.IsOn = connection.Search; UpdateSearch();
            _delete.IsEnabled = _store.State.Connections.Any(item => item.Id == connection.Id);
        }
        catch (Exception) { Report("无法读取凭据，请重新输入 API Key 后保存。", true); _key.Password = ""; }
        finally { _loading = false; }
    }
    private AiConnection Read() => _editing with { Name = _name.Text.Trim(), BaseUrl = _address.Text.Trim(),
        ModelsUrl = _modelsAddress.Text.Trim(), Format = (AiApiFormat)_format.SelectedIndex,
        Model = _model.Text.Trim(), Search = _search.IsOn, SendImages = _images.IsOn, SendFiles = _files.IsOn,
        Reasoning = _reasoning.SelectedIndex switch { 1 => "supported", 2 => "unsupported", _ => "auto" },
        MaxOutputTokens = double.IsFinite(_maxTokens.Value) ? (int)_maxTokens.Value : 8192 };

    private void UpdateSearch()
    {
        if (_editing is null || _format.SelectedIndex < 0) return;
        bool available = AiProviderCatalog.SupportsSearch(Read());
        _search.IsEnabled = available;
        if (!available) _search.IsOn = false;
        ToolTipService.SetToolTip(_search, available ? "由供应商执行联网检索，结果能力取决于所选模型。" : "此供应商与协议组合尚未配置联网搜索参数。");
    }
    private void Refresh()
    {
        _loading = true;
        _connections.Items.Clear();
        foreach (var connection in _store.State.Connections)
        {
            string icon = AiProviderCatalog.Presets.FirstOrDefault(provider => provider.Id == connection.Provider)?.Icon ?? "newapi";
            var item = new ComboBoxItem { Tag = connection.Id, Content = ProviderIcon.Label(icon, connection.Name) };
            _connections.Items.Add(item);
            if (connection.Id == _store.State.ActiveId) _connections.SelectedItem = item;
        }
        var active = _store.Active;
        _active.Text = active is null ? "当前使用环境变量配置或本地演示。" : $"当前连接  {active.Name} · {active.Model}";
        _loading = false;
    }
    private async Task RequestAsync(bool fetch)
    {
        _request?.Cancel();
        using var cancellation = new CancellationTokenSource(); _request = cancellation;
        int generation = ++_generation;
        SetBusy(true); Report(fetch ? "正在获取模型列表…" : "正在测试连接…", false);
        try
        {
            var connection = Read();
            using var client = new AiProviderClient();
            if (fetch)
            {
                var models = await client.ModelsAsync(connection, _key.Password, cancellation.Token);
                if (generation != _generation) return;
                string selected = _model.Text;
                _model.Items.Clear();
                foreach (var model in models)
                    _model.Items.Add(new ComboBoxItem { Tag = model.Id, Content = model.Id == model.Name ? model.Id : model.Name + " · " + model.Id });
                _model.SelectedItem = _model.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == selected);
                _model.Text = selected;
                Report($"已获取 {models.Count} 个模型，请选择后保存。", false);
            }
            else
            {
                await client.CompleteAsync(connection with { Search = false, MaxOutputTokens = 256 }, _key.Password,
                    new("Reply briefly.", "Reply OK.", Array.Empty<Core.AiAssistant.AssistantMessage>(), Array.Empty<string>()), cancellation.Token);
                if (generation == _generation) Report("连接与模型调用成功。", false);
            }
        }
        catch (OperationCanceledException) { if (generation == _generation) Report("已取消请求或连接超时。", true); }
        catch (Exception error) { if (generation == _generation) Report(error is HttpRequestException or InvalidDataException or ArgumentException
            ? error.Message : "连接失败，请检查接口、模型和 API Key。", true); }
        finally { if (generation == _generation) SetBusy(false); if (ReferenceEquals(_request, cancellation)) _request = null; }
    }
    private void SetBusy(bool busy) { _fetch.IsEnabled = _test.IsEnabled = _save.IsEnabled = !busy; }
    private void Execute(Action action)
    {
        try { action(); }
        catch (Exception error) { Report(error is ArgumentException ? error.Message : "保存连接失败，请检查配置目录和 Windows 凭据管理器。", true); }
    }
    private void Report(string message, bool error)
    { _status.Message = message; _status.Severity = error ? InfoBarSeverity.Error : InfoBarSeverity.Informational; _status.IsOpen = true; }
    private void SetNames()
    {
        foreach (var (control, name) in new (DependencyObject, string)[] { (_connections, "已保存连接"), (_preset, "供应商预设"),
            (_name, "连接名称"), (_address, "API 地址"), (_modelsAddress, "模型列表地址"), (_format, "API 格式"),
            (_key, "API Key"), (_model, "模型"), (_fetch, "获取模型列表"), (_test, "测试连接"), (_save, "保存并使用"),
            (_delete, "删除连接"), (_images, "发送截图给模型"), (_search, "联网搜索") }) AutomationProperties.SetName(control, name);
    }
}
