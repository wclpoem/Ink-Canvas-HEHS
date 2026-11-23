using System.Diagnostics;
using System.IO.Pipes;
using InkCanvas.Extensibility;

namespace InkCanvas.Extensions;

/// <summary>One worker per enabled plugin. The UI never loads external assemblies.</summary>
public sealed class PluginManager : IDisposable
{
    private sealed class Plugin
    {
        public ExtensionManifest Manifest;
        public string Directory;
        public PluginClient Client;
        public ExtensionState State = ExtensionState.Installed;
        public string Error;
        public PluginCommand[] Commands = Array.Empty<PluginCommand>();
        public bool Eligible;
    }
    private readonly Dictionary<string, Plugin> _plugins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _desired = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _changes = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _worker, _dataRoot;
    private readonly TimeSpan _timeout;
    private bool _disposed;
    public PluginManager(string workerExecutable, string dataRoot, TimeSpan? timeout = null)
    { _worker = workerExecutable; _dataRoot = dataRoot; _timeout = timeout ?? TimeSpan.FromSeconds(5); }
    public event Action Changed;
    public IReadOnlyList<ExtensionStatus> Status { get { lock (_plugins) return _plugins.Values.Select(p => new ExtensionStatus(p.Manifest.Id, p.Manifest.Name, p.Manifest.Version, p.State, p.Error)).ToArray(); } }
    public IReadOnlyList<ExtensionCommand> Commands
    {
        get { lock (_plugins) return _plugins.Values.Where(p => p.State == ExtensionState.Running).SelectMany(p => p.Commands.Select(c => new ExtensionCommand(p.Manifest.Id, c.Id, c.Title, c.Description))).ToArray(); }
    }
    public void Discover(params string[] roots) => DiscoverCore(roots, null);

    public void DiscoverManaged(string appRoot, string userRoot)
        => DiscoverCore(new[] { userRoot, appRoot }, Path.GetFullPath(userRoot));

    private void DiscoverCore(string[] roots, string userRoot)
    {
        if (_plugins.Count > 0) throw new InvalidOperationException("Discover plugin packages once before startup.");
        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            string[] directories;
            try { directories = Directory.GetDirectories(root); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                string id = "unavailable-directory-" + _plugins.Count;
                _plugins.Add(id, new Plugin { Manifest = new ExtensionManifest { Id = id, Name = "插件目录不可读", Version = "0.0.0" }, State = ExtensionState.Failed, Error = ex.Message });
                continue;
            }
            foreach (string directory in directories)
            {
                string path = Path.Combine(directory, "plugin.json");
                if (!File.Exists(path)) continue;
                ExtensionManifest manifest = null;
                try
                {
                    manifest = ExtensionPackages.Read(path);
                    ExtensionPackages.EntryPath(directory, manifest.EntryAssembly);
                    if (manifest.Dependencies.Length != 0) throw new InvalidDataException("External plugins must be independent packages. Share functionality through the versioned SDK.");
                    if (_plugins.TryGetValue(manifest.Id, out var duplicate))
                    {
                        if (userRoot is not null && string.Equals(Path.GetDirectoryName(duplicate.Directory), userRoot, StringComparison.OrdinalIgnoreCase)
                            && File.Exists(Path.Combine(duplicate.Directory, ".repository-package.json")))
                        {
                            if (Version.Parse(manifest.Version) > Version.Parse(duplicate.Manifest.Version))
                                _plugins[manifest.Id] = new Plugin { Manifest = manifest, Directory = directory, Eligible = true };
                            continue;
                        }
                        duplicate.Eligible = false; duplicate.State = ExtensionState.Failed; duplicate.Error = "Duplicate plugin package ID."; continue;
                    }
                    _plugins.Add(manifest.Id, new Plugin { Manifest = manifest, Directory = directory, Eligible = true });
                }
                catch (Exception ex)
                {
                    string id = manifest?.Id ?? "invalid-" + Path.GetFileName(directory);
                    if (!_plugins.ContainsKey(id)) _plugins.Add(id, new Plugin
                    { Manifest = manifest ?? new ExtensionManifest { Id = id, Name = Path.GetFileName(directory), Version = "0.0.0" }, Directory = directory, State = ExtensionState.Failed, Error = ex.Message });
                }
            }
        }
    }
    public async Task StartEnabledAsync(IReadOnlySet<string> enabled)
    {
        var ids = Status.Where(status => status.State != ExtensionState.Failed).Select(status => status.Id).ToArray();
        lock (_plugins) foreach (string id in ids) _desired.TryAdd(id, enabled.Contains(id));
        foreach (var id in ids)
        {
            if (_disposed) return;
            await ApplyDesiredAsync(id);
        }
        Notify();
    }
    public async Task SetEnabledAsync(string id, bool enabled)
    {
        lock (_plugins) _desired[id] = enabled;
        await ApplyDesiredAsync(id);
    }
    private async Task ApplyDesiredAsync(string id)
    {
        await _changes.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Plugin plugin; lock (_plugins) plugin = _plugins.TryGetValue(id, out var value) ? value : throw new KeyNotFoundException(id);
            bool enabled; lock (_plugins) enabled = _desired.GetValueOrDefault(id);
            if (enabled && !plugin.Eligible) throw new InvalidDataException(plugin.Error ?? "Invalid plugin package.");
            if (!enabled)
            {
                PluginClient client;
                lock (_plugins) { client = plugin.Client; plugin.Client = null; plugin.Commands = Array.Empty<PluginCommand>(); plugin.State = ExtensionState.Disabled; }
                if (client is not null) { await client.ShutdownAsync(); client.Dispose(); }
                Notify(); return;
            }
            if (plugin.State == ExtensionState.Running) return;
            lock (_plugins) { plugin.State = ExtensionState.Starting; plugin.Error = null; }
            Notify();
            var next = new PluginClient(_worker, plugin.Directory, Path.Combine(_dataRoot, "plugin-" + id), _timeout);
            try
            {
                var response = await next.StartAsync(_shutdown.Token);
                if (!response.Success) throw new InvalidDataException(response.Error);
                if (response.Commands is null || response.Commands.Length > 32 || response.Commands.Any(c => c is null || string.IsNullOrWhiteSpace(c.Id)
                    || c.Id.Length > 64 || string.IsNullOrWhiteSpace(c.Title) || c.Title.Length > 60 || c.Description?.Length > 500)
                    || response.Commands.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != response.Commands.Length)
                    throw new InvalidDataException("Plugin registered invalid or duplicate commands.");
                lock (_plugins)
                {
                    if (_disposed) throw new ObjectDisposedException(nameof(PluginManager));
                    if (!_desired.GetValueOrDefault(id)) { next.Dispose(); plugin.State = ExtensionState.Disabled; return; }
                    plugin.Client = next; plugin.Commands = response.Commands; plugin.State = ExtensionState.Running;
                }
                next.Exited += () => Fault(plugin, next, "Plugin worker exited.");
                if (next.HasExited) Fault(plugin, next, "Plugin worker exited during initialization.");
            }
            catch (Exception ex) { next.Dispose(); lock (_plugins) { plugin.State = _desired.GetValueOrDefault(id) ? ExtensionState.Failed : ExtensionState.Disabled; plugin.Error = ex.GetBaseException().Message; } }
            Notify();
        }
        finally { _changes.Release(); }
    }
    public async Task<PluginCommandResult> ExecuteAsync(string id, string commandId, PluginCommandContext context, CancellationToken cancellationToken = default)
    {
        Plugin plugin; PluginClient client;
        lock (_plugins)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            plugin = _plugins.TryGetValue(id, out var value) ? value : throw new KeyNotFoundException(id);
            if (plugin.State != ExtensionState.Running || !plugin.Commands.Any(command => command.Id == commandId)) throw new InvalidOperationException("Plugin command is unavailable.");
            client = plugin.Client;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        try
        {
            var response = await client.RequestAsync(new PluginRequest("execute", commandId, context), cancellation.Token);
            if (!response.Success) throw new InvalidOperationException(response.Error);
            PluginWire.ValidateResult(response.Result, plugin.Manifest.Capabilities);
            lock (_plugins) if (plugin.Client != client || plugin.State != ExtensionState.Running) throw new OperationCanceledException("Plugin was disabled while executing.");
            return response.Result;
        }
        catch (Exception ex) { Fault(plugin, client, ex.GetBaseException().Message); throw; }
    }
    public string[] ImportExtensions
    {
        get
        {
            lock (_plugins) return _plugins.Values.Where(p => p.State == ExtensionState.Running)
                .SelectMany(p => p.Manifest.ImportExtensions).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    // A conversion uses a host-owned temporary file. Large media never travel over the command pipe.
    public async Task ConvertFileAsync(string input, string output)
    {
        Plugin plugin;
        PluginClient client;
        lock (_plugins)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var matches = _plugins.Values.Where(p => p.State == ExtensionState.Running
                && p.Manifest.ImportExtensions.Contains(Path.GetExtension(input), StringComparer.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException(matches.Length == 0
                ? "请先启用相应的文件导入插件。" : "多个已启用插件支持此格式，请只启用其中一个。");
            plugin = matches[0];
            client = plugin.Client;
        }
        PluginResponse response;
        try
        {
            response = await client.RequestAsync(new PluginRequest("import") { SourcePath = input, DestinationPath = output },
                _shutdown.Token, TimeSpan.FromMinutes(2));
        }
        catch (Exception ex) { Fault(plugin, client, ex.GetBaseException().Message); throw; }
        if (!response.Success) throw new InvalidDataException(response.Error);
        lock (_plugins) if (plugin.Client != client || plugin.State != ExtensionState.Running)
            throw new OperationCanceledException("文件导入插件已停用。");
    }

    private void Fault(Plugin plugin, PluginClient client, string error)
    {
        lock (_plugins)
        {
            if (plugin.Client != client || _disposed) return;
            plugin.Client = null; plugin.Commands = Array.Empty<PluginCommand>(); plugin.State = ExtensionState.Failed; plugin.Error = error;
        }
        client.Dispose(); Notify();
    }
    private void Notify() { foreach (Action observer in Changed?.GetInvocationList() ?? Array.Empty<Delegate>()) try { observer(); } catch { } }
    public void Dispose()
    {
        PluginClient[] clients;
        lock (_plugins)
        {
            if (_disposed) return; _disposed = true;
            clients = _plugins.Values.Select(plugin => plugin.Client).Where(client => client is not null).ToArray();
            foreach (var plugin in _plugins.Values) { plugin.Client = null; plugin.Commands = Array.Empty<PluginCommand>(); }
        }
        // Process exit callbacks also take _plugins. Never dispose a process while holding that lock.
        _shutdown.Cancel();
        foreach (var client in clients) client.Dispose();
    }
}

internal sealed class PluginClient : IDisposable
{
    private readonly string _worker, _package, _data;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private NamedPipeServerStream _pipe;
    private Process _process;
    private int _disposed;
    public event Action Exited;
    public PluginClient(string worker, string package, string data, TimeSpan timeout) { _worker = worker; _package = package; _data = data; _timeout = timeout; }
    public bool HasExited { get { try { return _process?.HasExited != false; } catch (InvalidOperationException) { return true; } } }
    public async Task<PluginResponse> StartAsync(CancellationToken token)
    {
        string name = "InkCanvas-" + Guid.NewGuid().ToString("N");
        _pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(_worker) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(_worker)! };
        start.ArgumentList.Add("--pipe"); start.ArgumentList.Add(name);
        start.ArgumentList.Add("--package"); start.ArgumentList.Add(_package);
        start.ArgumentList.Add("--data"); start.ArgumentList.Add(_data);
        start.ArgumentList.Add("--parent"); start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _process = new Process { StartInfo = start, EnableRaisingEvents = true };
        _process.Exited += (_, _) => Exited?.Invoke();
        if (!_process.Start()) throw new InvalidOperationException("Plugin worker did not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(_timeout);
        await _pipe.WaitForConnectionAsync(timeout.Token);
        return await RequestAsync(new PluginRequest("initialize"), token);
    }
    public async Task<PluginResponse> RequestAsync(PluginRequest request, CancellationToken token, TimeSpan? requestTimeout = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(requestTimeout ?? _timeout);
        await _requests.WaitAsync(timeout.Token);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await PluginWire.WriteAsync(_pipe, request, timeout.Token);
            return await PluginWire.ReadAsync<PluginResponse>(_pipe, timeout.Token);
        }
        finally { _requests.Release(); }
    }
    public async Task ShutdownAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try { await RequestAsync(new PluginRequest("shutdown"), timeout.Token); } catch { }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _pipe?.Dispose();
        try
        {
            if (_process is not null)
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                // Kill is asynchronous. Release loaded package files before disposal returns.
                _process.WaitForExit(1000);
            }
        }
        catch (InvalidOperationException) { }
        _process?.Dispose();
    }
}
