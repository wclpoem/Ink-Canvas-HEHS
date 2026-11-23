using InkCanvas.Controls.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.Modules.CloudSync;

internal sealed partial class CloudSyncService
{
    public FrameworkElement CreateSettingsView()
    {
        var panel = new StackPanel { Spacing = 12 };
        var service = new TextBox { Header = "HTTPS 同步服务地址", Text = _state.ServiceUrl, PlaceholderText = "由学校或服务管理员提供", MinHeight = 44 };
        var email = new TextBox { Header = "账号", Text = _state.Email, MinHeight = 44 };
        var password = new PasswordBox { Header = "密码", MinHeight = 44 };
        var invitation = new PasswordBox { Header = "注册邀请码（已有账号可留空）", MinHeight = 44 };
        var notice = new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational,
            Message = "服务地址默认留空。登录后手动开启云同步，将白板和所选偏好上传至该服务。账号密钥保存在 Windows 凭据管理器。" };
        var status = new TextBlock { Text = _status, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var enabled = new ToggleSwitch { Header = "自动备份白板到云端", IsOn = _state.Enabled };
        var preferences = new ToggleSwitch { Header = "同步个人书写和外观偏好", IsOn = _state.SyncPreferences };
        var interval = new NumberBox { Header = "自动保存与重试间隔（分钟）", Minimum = 1, Maximum = 120, Value = _state.IntervalMinutes,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var files = new StackPanel { Spacing = 8 };
        var buttons = new List<Button>();
        bool busy = false;
        void Update()
        {
            status.Text = _status;
            bool signedIn = !string.IsNullOrWhiteSpace(_state.UserId);
            enabled.IsEnabled = preferences.IsEnabled = interval.IsEnabled = signedIn && !busy;
            service.IsEnabled = email.IsEnabled = password.IsEnabled = invitation.IsEnabled = !busy;
            foreach (var button in buttons) button.IsEnabled = !busy;
        }
        async Task Run(Func<Task> action)
        {
            if (busy || !await _gate.WaitAsync(0)) return;
            busy = true; Update();
            try { await action(); }
            catch (Exception error) { Status(error is ArgumentException or InvalidOperationException or IOException ? error.Message : "操作未完成，请检查连接或稍后重试。"); }
            finally { busy = false; _gate.Release(); if (!_disposed) Update(); }
        }
        Button Action(string text, Func<Task> action)
        {
            var button = new Button { Content = text, MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(button, text);
            button.Click += async (_, _) => await Run(action);
            buttons.Add(button);
            return button;
        }
        var signIn = Action("登录 / 使用邀请码注册", async () =>
        {
            string endpoint = ValidateServiceUrl(service.Text);
            string account = email.Text.Trim().ToLowerInvariant();
            if (password.Password.Length < 12) throw new ArgumentException("密码至少需要 12 个字符。");
            string previousEndpoint = _state.ServiceUrl;
            _state.ServiceUrl = endpoint;
            LoginResult login;
            try
            {
                login = string.IsNullOrWhiteSpace(invitation.Password)
                    ? await SendAsync<LoginResult>(HttpMethod.Post, "v1/auth/login", new { email = account, password = password.Password }, false)
                    : await SendAsync<LoginResult>(HttpMethod.Post, "v1/auth/register", new { email = account, password = password.Password, invitation = invitation.Password }, false);
            }
            catch { _state.ServiceUrl = previousEndpoint; throw; }
            if (_state.ServiceUrl != previousEndpoint || login.User.Id != _state.UserId)
            {
                _state.Pending.Clear(); _state.PreferenceVersion = 0;
                _state.Enabled = _state.SyncPreferences = _state.PreferencesDirty = false;
                enabled.IsOn = preferences.IsOn = false;
            }
            AcceptLogin(login);
            password.Password = invitation.Password = "";
            email.Text = _state.Email;
            files.Children.Clear();
            Status("已登录 " + _state.Email + "，可开启自动备份。");
        });
        enabled.Toggled += (_, _) => { if (busy) return; _state.Enabled = enabled.IsOn; Save(); if (enabled.IsOn) _ = SyncAsync(); };
        preferences.Toggled += (_, _) => { if (busy) return; _state.SyncPreferences = preferences.IsOn; _state.PreferencesDirty = preferences.IsOn; Save(); };
        interval.ValueChanged += (_, args) =>
        {
            if (busy || !double.IsFinite(args.NewValue)) return;
            _state.IntervalMinutes = (int)Math.Clamp(args.NewValue, 1, 120);
            _timer.Interval = TimeSpan.FromMinutes(_state.IntervalMinutes); Save();
        };
        var sync = new Button { Content = "立即同步", MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(sync, "立即同步");
        sync.Click += async (_, _) => await SyncAsync();
        var list = Action("查看云端文稿", async () =>
        {
            var result = await SendAsync<FileList>(HttpMethod.Get, "v1/files?limit=100");
            files.Children.Clear();
            foreach (var file in result.Files)
            {
                var download = new Button { Content = "恢复副本", MinHeight = 44 };
                download.Click += async (_, _) => await Run(() => DownloadAsync(file));
                files.Children.Add(new SettingsRow(file.Name, download, 0,
                    file.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + " · " + (file.Size / 1024d / 1024).ToString("0.0") + " MiB"));
            }
            if (result.Files.Length == 0) files.Children.Add(new TextBlock { Text = "尚无云端文稿。" });
            Status(result.NextBefore is null ? "云端文稿已刷新。" : "显示最近 100 份文稿，历史版本按保存时间保留。");
        });
        var restore = Action("恢复云端偏好到本机", () => RestorePreferencesAsync(false));
        var publish = Action("确认以本机偏好更新云端", () => RestorePreferencesAsync(true));
        var logout = Action("注销当前设备", async () =>
        {
            try { await SendAsync<System.Text.Json.JsonElement>(HttpMethod.Post, "v1/auth/logout"); }
            finally
            {
                StoreRefresh(null); _accessToken = null; _state.Enabled = _state.SyncPreferences = false; _state.UserId = "";
                _state.Pending.Clear(); _state.PreferenceVersion = 0; _state.PreferencesDirty = false;
                enabled.IsOn = preferences.IsOn = false; Save(); files.Children.Clear(); Status("已注销当前设备。");
            }
        });
        panel.Children.Add(notice);
        foreach (var control in new UIElement[] { service, email, password, invitation, signIn, enabled, preferences, interval,
            sync, status, list, files, restore, publish, logout }) panel.Children.Add(control);
        panel.Loaded += (_, _) => { Changed += Update; Update(); };
        panel.Unloaded += (_, _) => Changed -= Update;
        Update();
        return panel;
    }
}
