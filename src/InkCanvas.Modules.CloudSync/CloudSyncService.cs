using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InkCanvas.Host.Contracts;
using Microsoft.UI.Dispatching;
using Windows.Security.Credentials;

namespace InkCanvas.Modules.CloudSync;

internal sealed partial class CloudSyncService : ICloudSyncService, IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly ICloudSyncHost _host;
    private readonly HttpClient _api = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) };
    private readonly HttpClient _objects = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(20) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherQueueTimer _timer;
    private readonly string _statePath;
    private SyncState _state;
    private string _accessToken;
    private DateTimeOffset _accessUntil;
    private bool _disposed, _applyingPreferences;
    private string _status = "填写服务地址并登录后，可开启云同步。";
    private event Action Changed;

    internal CloudSyncService(ICloudSyncHost host)
    {
        _host = host;
        Directory.CreateDirectory(host.DataDirectory);
        _statePath = Path.Combine(host.DataDirectory, "sync.json");
        try { _state = JsonSerializer.Deserialize<SyncState>(File.ReadAllText(_statePath), Json) ?? new(); }
        catch (Exception error) when (error is IOException or JsonException) { _state = new(); }
        ApplyDeploymentPolicy();
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMinutes(Math.Clamp(_state.IntervalMinutes, 1, 120));
        _timer.Tick += (_, _) => _ = SyncAsync();
        _timer.Start();
        host.BoardSaved += OnBoardSaved;
        host.PreferencesSaved += OnPreferencesSaved;
    }

    private void ApplyDeploymentPolicy()
    {
        string policyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "InkCanvas", "cloud-policy.json");
        if (!File.Exists(policyPath)) return;
        try
        {
            var policy = JsonNode.Parse(File.ReadAllText(policyPath));
            if (string.IsNullOrWhiteSpace(_state.ServiceUrl)) _state.ServiceUrl = ValidateServiceUrl(policy?["serviceUrl"]?.GetValue<string>());
            _state.IntervalMinutes = Math.Clamp(policy?["syncIntervalMinutes"]?.GetValue<int>() ?? 5, 1, 120);
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or ArgumentException)
        { _status = "学校部署配置无效，请联系管理员检查。"; }
    }

    internal static string ValidateServiceUrl(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("请输入有效的 HTTPS 服务地址。");
        return uri.AbsoluteUri.TrimEnd('/') + "/";
    }

    private string CredentialAccount => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_state.ServiceUrl))) + ":" + _state.Email;
    private string RefreshToken()
    {
        try { var credential = new PasswordVault().Retrieve("InkCanvas.CloudSync", CredentialAccount); credential.RetrievePassword(); return credential.Password; }
        catch { return null; }
    }
    private void StoreRefresh(string value)
    {
        var vault = new PasswordVault();
        try { vault.Remove(vault.Retrieve("InkCanvas.CloudSync", CredentialAccount)); } catch { }
        if (!string.IsNullOrEmpty(value)) vault.Add(new PasswordCredential("InkCanvas.CloudSync", CredentialAccount, value));
    }
    private void Save()
    {
        string temporary = _statePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_state, Json));
        File.Move(temporary, _statePath, true);
    }
    private void Status(string text) { if (_disposed) return; _status = text; Changed?.Invoke(); }
    private void OnBoardSaved(string path)
    {
        if (_disposed || !_state.Enabled || string.IsNullOrWhiteSpace(_state.UserId)) return;
        if (!Path.GetExtension(path).Equals(".icink", StringComparison.OrdinalIgnoreCase)) return;
        if (!_state.Pending.Contains(path)) _state.Pending.Add(path);
        Save();
        _ = SyncAsync();
    }
    private void OnPreferencesSaved()
    {
        if (_applyingPreferences || !_state.SyncPreferences) return;
        _state.PreferencesDirty = true;
        Save();
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object data = null, bool authenticated = true, CancellationToken token = default)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        token = operation.Token;
        if (authenticated) await EnsureAccessAsync(token);
        using var request = new HttpRequestMessage(method, new Uri(new Uri(ValidateServiceUrl(_state.ServiceUrl)), path));
        if (authenticated) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        if (data is not null) request.Content = JsonContent.Create(data, options: Json);
        using var response = await _api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.Conflict) throw new SyncConflictException();
        if (response.StatusCode == HttpStatusCode.Unauthorized) { _accessToken = null; throw new InvalidOperationException("账号或密码无效，或登录已过期，请重新登录。"); }
        if (!response.IsSuccessStatusCode) throw new IOException("云同步请求失败（" + (int)response.StatusCode + "），请检查账号、配额或稍后重试。");
        return await response.Content.ReadFromJsonAsync<T>(Json, token) ?? throw new IOException("云端响应为空。");
    }
    private async Task EnsureAccessAsync(CancellationToken token = default)
    {
        if (_accessToken is not null && _accessUntil > DateTimeOffset.UtcNow.AddSeconds(30)) return;
        string refresh = RefreshToken() ?? throw new InvalidOperationException("请先登录云同步账号。");
        var login = await SendAsync<LoginResult>(HttpMethod.Post, "v1/auth/refresh", new { refreshToken = refresh }, false, token);
        AcceptLogin(login);
    }
    private void AcceptLogin(LoginResult login)
    {
        _accessToken = login.AccessToken;
        _accessUntil = DateTimeOffset.UtcNow.AddSeconds(login.ExpiresIn);
        _state.UserId = login.User.Id;
        _state.Email = login.User.Email;
        StoreRefresh(login.RefreshToken);
        Save();
    }

    public Task SyncAsync() => SyncCoreAsync(CancellationToken.None, reportErrors: true);
    internal Task SyncForAssistantAsync(CancellationToken token) => SyncCoreAsync(token, reportErrors: false);

    private async Task SyncCoreAsync(CancellationToken token, bool reportErrors)
    {
        if (_disposed && reportErrors) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        token = operation.Token;
        token.ThrowIfCancellationRequested();
        if (_disposed || !(_state.Enabled || _state.SyncPreferences) || string.IsNullOrWhiteSpace(_state.UserId) || !await _gate.WaitAsync(0, token))
        {
            if (!reportErrors) throw new InvalidOperationException("云同步尚未就绪或已有同步正在运行。");
            return;
        }
        try
        {
            await EnsureAccessAsync(token);
            Status("正在准备云端备份…");
            if (_state.Enabled)
            {
                string snapshot = await _host.SaveSnapshotAsync(token);
                if (snapshot is not null && !_state.Pending.Contains(snapshot)) _state.Pending.Add(snapshot);
                Save();
                foreach (string path in _state.Pending.ToArray())
                {
                    if (!File.Exists(path)) { _state.Pending.Remove(path); Save(); continue; }
                    Status("正在上传 " + Path.GetFileName(path));
                    await UploadAsync(path, token);
                    _state.Pending.Remove(path);
                    Save();
                }
            }
            if (_state.SyncPreferences && _state.PreferencesDirty)
            {
                try
                {
                    using var portable = JsonDocument.Parse(_host.ExportPreferences());
                    var saved = await SendAsync<PreferenceResult>(HttpMethod.Put, "v1/preferences", new { version = _state.PreferenceVersion, values = portable.RootElement }, token: token);
                    _state.PreferenceVersion = saved.Version;
                    _state.PreferencesDirty = false;
                    Save();
                }
                catch (SyncConflictException)
                {
                    Status("白板已备份。配置与另一设备冲突，请先恢复云端配置，或确认以本机配置更新。");
                    if (!reportErrors) throw;
                    return;
                }
            }
            _state.LastSyncUtc = DateTimeOffset.UtcNow;
            Save();
            Status("同步完成 · " + _state.LastSyncUtc.Value.ToLocalTime().ToString("HH:mm:ss"));
        }
        catch (OperationCanceledException) when (_disposed || reportErrors) { }
        catch (Exception error) when (reportErrors) { Status(error is IOException or InvalidOperationException or ArgumentException ? error.Message : "同步暂未完成，将在下次自动重试。"); }
        finally { _gate.Release(); }
    }

    private async Task UploadAsync(string path, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (input.Length > 536870912) throw new IOException("此白板超过 512 MiB，请减少视频等媒体后重试。");
        string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token));
        input.Position = 0;
        var ticket = await SendAsync<UploadTicket>(HttpMethod.Post, "v1/files/uploads", new { name = Path.GetFileName(path), sha256 = hash, size = input.Length }, token: token);
        if (ticket.Complete) return;
        using var request = new HttpRequestMessage(HttpMethod.Put, ObjectUri(ticket.UploadUrl));
        request.Content = new StreamContent(input, 81920);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentLength = input.Length;
        foreach (var header in ticket.Headers ?? new())
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
            { request.Content.Headers.Remove(header.Key); request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value); }
        using var response = await _objects.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Conflict) throw new IOException("白板上传暂未完成，下次将重试。");
        await SendAsync<CloudFile>(HttpMethod.Post, "v1/files/" + ticket.Id + "/commit", token: token);
    }
    private static Uri ObjectUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo))
            throw new IOException("云端返回了不安全的文件地址。");
        return uri;
    }
    private async Task RestorePreferencesAsync(bool uploadLocal)
    {
        var cloud = await SendAsync<PreferenceResult>(HttpMethod.Get, "v1/preferences");
        if (uploadLocal)
        {
            using var local = JsonDocument.Parse(_host.ExportPreferences());
            cloud = await SendAsync<PreferenceResult>(HttpMethod.Put, "v1/preferences", new { version = cloud.Version, values = local.RootElement });
        }
        else
        {
            _applyingPreferences = true;
            try { _host.ImportPreferences(cloud.Values.GetRawText()); }
            finally { _applyingPreferences = false; }
        }
        _state.PreferenceVersion = cloud.Version;
        _state.PreferencesDirty = false;
        Save();
        Status(uploadLocal ? "已确认使用本机配置更新云端。" : "已恢复云端偏好。");
    }
    private async Task DownloadAsync(CloudFile file)
    {
        var ticket = await SendAsync<DownloadTicket>(HttpMethod.Get, "v1/files/" + file.Id + "/download");
        string directory = Path.Combine(_host.DefaultDirectory, "CloudDownloads");
        Directory.CreateDirectory(directory);
        string name = Path.GetFileName(file.Name);
        if (name != file.Name || !name.EndsWith(".icink", StringComparison.OrdinalIgnoreCase)) throw new IOException("云端文件名无效。");
        string destination = Path.Combine(directory, Path.GetFileNameWithoutExtension(name) + "-" + Guid.NewGuid().ToString("N")[..8] + ".icink");
        string temporary = destination + ".partial";
        try
        {
            using var response = await _objects.GetAsync(ObjectUri(ticket.DownloadUrl), HttpCompletionOption.ResponseHeadersRead, _lifetime.Token);
            response.EnsureSuccessStatusCode();
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            await using (var stream = await response.Content.ReadAsStreamAsync(_lifetime.Token))
            {
                byte[] buffer = new byte[81920]; long bytes = 0;
                int count;
                while ((count = await stream.ReadAsync(buffer, _lifetime.Token)) > 0)
                {
                    bytes += count;
                    if (bytes > file.Size || bytes > 536870912) throw new IOException("云端文件大小校验失败。");
                    await output.WriteAsync(buffer.AsMemory(0, count), _lifetime.Token);
                }
                if (bytes != file.Size) throw new IOException("云端文件不完整。");
            }
            await using (var input = File.OpenRead(temporary))
                if (Convert.ToHexStringLower(await SHA256.HashDataAsync(input, _lifetime.Token)) != file.Sha256) throw new IOException("云端文件完整性校验失败。");
            File.Move(temporary, destination);
            Status("已恢复副本至 " + destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop(); _lifetime.Cancel();
        _host.BoardSaved -= OnBoardSaved;
        _host.PreferencesSaved -= OnPreferencesSaved;
        _api.Dispose(); _objects.Dispose();
    }
    private sealed class SyncConflictException : IOException { }
    private sealed class SyncState
    {
        public string ServiceUrl { get; set; } = "";
        public string Email { get; set; } = "";
        public string UserId { get; set; } = "";
        public bool Enabled { get; set; }
        public bool SyncPreferences { get; set; }
        public int IntervalMinutes { get; set; } = 5;
        public int PreferenceVersion { get; set; }
        public bool PreferencesDirty { get; set; }
        public DateTimeOffset? LastSyncUtc { get; set; }
        public List<string> Pending { get; set; } = new();
    }
    private sealed record Account(string Id, string Email);
    private sealed record LoginResult(string AccessToken, string RefreshToken, int ExpiresIn, Account User);
    private sealed record UploadTicket(string Id, string UploadUrl, Dictionary<string, string> Headers, bool Complete);
    private sealed record PreferenceResult(int Version, JsonElement Values);
    private sealed record CloudFile(string Id, string Name, string Sha256, long Size, DateTimeOffset CreatedAt);
    private sealed record FileList(CloudFile[] Files, double? NextBefore);
    private sealed record DownloadTicket(string DownloadUrl, CloudFile File);
}
