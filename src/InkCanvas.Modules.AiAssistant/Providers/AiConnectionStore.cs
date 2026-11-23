using System.Text.Json;
using Windows.Security.Credentials;

namespace InkCanvas.Modules.AiAssistant.Providers;

public interface IAiCredentialStore
{
    string Read(string id);
    void Write(string id, string key);
}

public sealed class AiCredentialVault : IAiCredentialStore
{
    private const string Resource = "InkCanvas.AiConnections";
    public string Read(string id)
    {
        try { var entry = new PasswordVault().Retrieve(Resource, id); entry.RetrievePassword(); return entry.Password; }
        catch (System.Runtime.InteropServices.COMException error) when ((uint)error.HResult == 0x80070490) { return ""; }
    }
    public void Write(string id, string key)
    {
        var vault = new PasswordVault();
        try { vault.Remove(vault.Retrieve(Resource, id)); }
        catch (System.Runtime.InteropServices.COMException error) when ((uint)error.HResult == 0x80070490) { }
        if (!string.IsNullOrWhiteSpace(key)) vault.Add(new PasswordCredential(Resource, id, key));
    }
}

public sealed class AiConnectionStore
{
    private readonly string _path;
    private readonly IAiCredentialStore _credentials;
    private readonly object _gate = new();
    private AiConnectionState _state;
    public AiConnectionStore(string directory, IAiCredentialStore credentials = null)
    {
        _path = Path.Combine(directory, "connections.json");
        _credentials = credentials ?? new AiCredentialVault();
        _state = File.Exists(_path) ? JsonSerializer.Deserialize<AiConnectionState>(File.ReadAllText(_path)) : null;
        _state ??= new("", Array.Empty<AiConnection>());
        if (_state.Connections is null) _state = _state with { Connections = Array.Empty<AiConnection>() };
    }
    public AiConnectionState State { get { lock (_gate) return _state with { Connections = _state.Connections.ToArray() }; } }
    public AiConnection Active
    {
        get { var state = State; return state.Connections.FirstOrDefault(connection => connection.Id == state.ActiveId); }
    }
    public string Key(string id) => _credentials.Read(id);

    public void Save(AiConnection connection, string key, bool activate = true)
    {
        if (!Guid.TryParseExact(connection.Id, "N", out _) || string.IsNullOrWhiteSpace(connection.Name)) throw new ArgumentException("连接名称无效。");
        if (activate && string.IsNullOrWhiteSpace(connection.Model)) throw new ArgumentException("请先获取或填写模型 ID，再保存并使用。");
        AiEndpoint.Validate(connection.BaseUrl);
        if (!string.IsNullOrWhiteSpace(connection.ModelsUrl)) AiEndpoint.Validate(connection.ModelsUrl);
        if (!Enum.IsDefined(connection.Format) || connection.MaxOutputTokens is < 256 or > 131072) throw new ArgumentException("连接参数无效。");
        lock (_gate)
        {
            string original = _credentials.Read(connection.Id);
            try
            {
                _credentials.Write(connection.Id, key);
                var list = _state.Connections.Where(item => item.Id != connection.Id).Append(connection).ToArray();
                Write(_state with { ActiveId = activate ? connection.Id : _state.ActiveId, Connections = list });
            }
            catch { _credentials.Write(connection.Id, original); throw; }
        }
    }
    public void Activate(string id)
    {
        lock (_gate)
        {
            if (id.Length > 0 && !_state.Connections.Any(item => item.Id == id)) throw new ArgumentException("连接不存在。");
            Write(_state with { ActiveId = id });
        }
    }
    public void Delete(string id)
    {
        lock (_gate)
        {
            Write(_state with { ActiveId = _state.ActiveId == id ? "" : _state.ActiveId,
                Connections = _state.Connections.Where(item => item.Id != id).ToArray() });
            _credentials.Write(id, "");
        }
    }
    private void Write(AiConnectionState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path));
        string pending = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(pending, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(pending, _path, overwrite: true);
            _state = state;
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
}
