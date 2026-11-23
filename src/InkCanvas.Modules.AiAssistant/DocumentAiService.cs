using System.Text.Json;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Board;
using InkCanvas.Host.Contracts;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using InkCanvas.Modules.AiAssistant.Providers;

namespace InkCanvas.Modules.AiAssistant;

internal sealed class DocumentAiService : IAiDocumentService, IDisposable
{
    private readonly IAiAssistantHost _assistant;
    private readonly IAiDocumentHost _host;
    private readonly DocumentNamingClient _client;
    private readonly AiConnectionStore _connections;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _automatic = new();
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AiDocumentRequest> _requeued = new(StringComparer.OrdinalIgnoreCase);
    private event Action StatusChanged;
    private bool _disposed;
    private AiDocumentSettings Settings => _assistant.Settings.Ai.Documents ??= new();
    public string Status { get; private set; } = "尚未进行 AI 存档命名。";
    public bool SupportsSuggestions => true;

    public DocumentAiService(IAiAssistantHost assistant, IAiDocumentHost host, AiConnectionStore connections = null)
    {
        _connections = connections;
        _client = new DocumentNamingClient(connections: connections);
        _assistant = assistant; _host = host; host.DocumentSaved += OnSaved;
        _ = ResumeAsync();
    }

    private async Task ResumeAsync()
    {
        try
        {
            await Task.Delay(1000, _lifetime.Token);
            string pending = Path.Combine(_host.DataDirectory, "pending.json");
            if (!Settings.Enabled || !File.Exists(pending)) return;
            var paths = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(pending, _lifetime.Token)) ?? Array.Empty<string>();
            foreach (string path in paths.Where(File.Exists).Take(32)) OnSaved(new(path));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetStatus("恢复 AI 命名任务失败 · " + ex.Message); }
    }

    private void OnSaved(AiDocumentRequest request)
    {
        if (_disposed || !Settings.Enabled || _queued.Count >= 32) return;
        if (!_queued.Add(request.Path)) { _requeued[request.Path] = request; return; }
        PersistQueue();
        _ = ProcessSavedAsync(request);
    }
    private async Task ProcessSavedAsync(AiDocumentRequest request)
    {
        bool completed = false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _automatic.Token);
        try
        {
            await Task.Delay(150, cancellation.Token);
            if (!Settings.Enabled) { completed = true; return; }
            await DescribeAsync(request, cancellation.Token); completed = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetStatus("AI 命名失败，存档已保留 · " + ex.GetBaseException().Message); completed = true; }
        finally
        {
            if (completed)
            {
                _queued.Remove(request.Path); PersistQueue();
                if (_requeued.Remove(request.Path, out var next)) OnSaved(next);
            }
        }
    }
    private void PersistQueue()
    {
        try
        {
            Directory.CreateDirectory(_host.DataDirectory);
            string destination = Path.Combine(_host.DataDirectory, "pending.json");
            File.WriteAllText(destination + ".tmp", JsonSerializer.Serialize(_queued.Where(path => Path.GetExtension(path).Equals(".icink", StringComparison.OrdinalIgnoreCase))));
            File.Move(destination + ".tmp", destination, true);
        }
        catch (Exception ex) { Core.Diagnostics.FileLog.WriteLogToFile("记录 AI 存档队列 " + ex.Message); }
    }

    public async Task<AiDocumentResult> DescribeAsync(AiDocumentRequest request, CancellationToken token = default)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        token = operation.Token;
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var settings = new AiDocumentSettings { Endpoint = Settings.Endpoint, Model = Settings.Model, AssistantProtocol = Settings.AssistantProtocol };
            if (_connections?.Active is null || !string.IsNullOrWhiteSpace(settings.Endpoint)) DocumentNamingClient.ValidateConfiguration(settings);
            var result = await Task.Run(() => ProcessFileAsync(request, settings, token), token);
            if (!request.PreviewOnly)
                try { _host.Completed(result); } catch (Exception ex) { Core.Diagnostics.FileLog.WriteLogToFile("同步 AI 文稿结果 " + ex.Message); }
            return result;
        }
        catch (OperationCanceledException) { SetStatus("已取消 AI 命名，存档已保留。"); throw; }
        catch (Exception ex) { SetStatus("AI 命名失败，存档已保留 · " + ex.GetBaseException().Message); throw; }
        finally { _gate.Release(); }
    }

    private async Task<AiDocumentResult> ProcessFileAsync(AiDocumentRequest request, AiDocumentSettings settings, CancellationToken token)
    {
        var stamp = new FileInfo(request.Path);
        long length = stamp.Length; DateTime written = stamp.LastWriteTimeUtc;
        string root = Path.Combine(_host.DataDirectory, "Images");
        string directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            SetStatus("正在生成文稿 PNG · " + stamp.Name);
            var images = request.ExportImages is not null ? request.ExportImages(directory, token) : ArchiveImageRenderer.ExportAsync(request.Path, directory, token);
            var batch = new List<AiDocumentImage>();
            var all = new List<AiDocumentImage>();
            var processed = new Queue<(string Path, long Bytes)>();
            long retainedBytes = 0;
            long batchBytes = 0;
            void RetireBatch()
            {
                foreach (var image in batch) processed.Enqueue((image.Path, new FileInfo(image.Path).Length));
                while (retainedBytes > 128L * 1024 * 1024 && processed.TryDequeue(out var entry))
                {
                    try { File.Delete(entry.Path); retainedBytes -= entry.Bytes; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            DocumentCaption caption = null;
            await foreach (var image in images.WithCancellation(token))
            {
                if (!Path.GetFullPath(image.Path).StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("插件应将 PNG 写入指定的临时目录。");
                long bytes = new FileInfo(image.Path).Length;
                if (bytes <= 0 || bytes > 32L * 1024 * 1024 || image.Width is <= 0 or > 3840 || image.Height is <= 0 or > 3840)
                    throw new InvalidDataException("插件返回的 PNG 超出图像限制。");
                if (batch.Count >= 4 || batchBytes + bytes > 32L * 1024 * 1024 && batch.Count > 0)
                {
                    SetStatus($"正在分析第 {all.Count - batch.Count + 1}–{all.Count} 张 PNG…");
                    caption = await _client.DescribeAsync(batch, caption?.Description, settings, token);
                    RetireBatch();
                    batch.Clear(); batchBytes = 0;
                }
                batch.Add(image); all.Add(image); batchBytes += bytes; retainedBytes += bytes;
            }
            if (batch.Count > 0)
            {
                caption = await _client.DescribeAsync(batch, caption?.Description, settings, token);
                RetireBatch();
            }
            if (caption is null) throw new InvalidDataException("文件中没有可供模型分析的页面。");
            token.ThrowIfCancellationRequested();
            string destination = request.Path;
            if (!request.PreviewOnly)
            {
                if (Path.GetExtension(request.Path).Equals(".icink", StringComparison.OrdinalIgnoreCase))
                    destination = await BoardService.DescribeArchiveAsync(request.Path, caption.Title, caption.Description, length, written, token);
                else
                {
                    stamp.Refresh();
                    if (stamp.Length != length || stamp.LastWriteTimeUtc != written) throw new IOException("文件已变化，此次命名结果已跳过。");
                    // PDF and construction editors retain open file handles. Their adapters receive the suggested name.
                    string details = request.Path + ".ai.json";
                    await File.WriteAllTextAsync(details + ".tmp", JsonSerializer.Serialize(caption), token);
                    File.Move(details + ".tmp", details, true);
                }
            }
            var result = new AiDocumentResult(request.Path, destination, caption.Title, caption.Description, all.Count, DateTimeOffset.UtcNow);
            try { await File.WriteAllTextAsync(Path.Combine(directory, "analysis.json"), JsonSerializer.Serialize(new { Result = result, Images = all })); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Core.Diagnostics.FileLog.WriteLogToFile("记录 AI 图像缓存 " + ex.Message); }
            SetStatus($"已生成名称和描述 · {caption.Title} · {all.Count} 张 PNG");
            return result;
        }
        finally { try { TrimCache(root, directory); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    private static void TrimCache(string root, string current)
    {
        long bytes = 0;
        foreach (var folder in new DirectoryInfo(root).EnumerateDirectories().OrderByDescending(folder => folder.CreationTimeUtc))
        {
            bytes += folder.EnumerateFiles().Sum(file => file.Length);
            if (bytes <= 256L * 1024 * 1024 || folder.FullName == current) continue;
            try { folder.Delete(true); } catch (IOException) { }
        }
    }
    private void SetStatus(string status)
    {
        Status = status;
        _dispatcher.TryEnqueue(() => { if (!_disposed) StatusChanged?.Invoke(); });
    }

    public FrameworkElement CreateSettingsView()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "AI 存档命名与描述", FontSize = 20 });
        var enabled = new ToggleSwitch { Header = "保存后自动分析并命名", IsOn = Settings.Enabled };
        panel.Children.Add(enabled);
        panel.Children.Add(new TextBlock { Text = "开启后会把存档生成的 PNG 发送到配置的多模态模型。白板文件会更新名称和描述，长页会分段发送。关闭时仍可在文稿库中手动执行。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "接口地址留空时沿用“AI 助手”设置中的当前连接、API Key 和模型，需启用截图输入并选择支持图片的模型。", TextWrapping = TextWrapping.Wrap });
        var endpoint = new TextBox { Header = "多模态接口完整地址", Text = Settings.Endpoint, PlaceholderText = "https://…/v1/chat/completions", MinHeight = 44 };
        var model = new TextBox { Header = "支持多图输入的模型名称", Text = Settings.Model, MinHeight = 44 };
        var protocol = new ComboBox { Header = "服务协议", ItemsSource = new[] { "OpenAI 兼容 Chat Completions", "现有 Ink Canvas 助教协议" }, SelectedIndex = Settings.AssistantProtocol ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var key = new PasswordBox { Header = "API 密钥", PlaceholderText = "留空使用已保存密钥或 AI_API_KEY 环境变量", MinHeight = 44 };
        var save = new Button { Content = "保存模型配置", MinHeight = 44 };
        var status = new TextBlock { Text = Status, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        enabled.Toggled += (_, _) =>
        {
            Settings.Enabled = enabled.IsOn;
            if (!enabled.IsOn)
            {
                _automatic.Cancel(); _automatic.Dispose(); _automatic = new();
                _queued.Clear(); _requeued.Clear(); PersistQueue();
            }
            _assistant.SaveSettings();
        };
        save.Click += (_, _) =>
        {
            Settings.Endpoint = endpoint.Text.Trim(); Settings.Model = model.Text.Trim(); Settings.AssistantProtocol = protocol.SelectedIndex == 1;
            try { if (!string.IsNullOrEmpty(key.Password)) { DocumentNamingClient.SaveCredential(key.Password); key.Password = ""; } _assistant.SaveSettings(); status.Text = "已保存模型配置，密钥存储在 Windows 凭据管理器中。"; }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        void Update() => status.Text = Status;
        panel.Loaded += (_, _) => StatusChanged += Update;
        panel.Unloaded += (_, _) => StatusChanged -= Update;
        foreach (var control in new UIElement[] { endpoint, model, protocol, key, save, status }) panel.Children.Add(control);
        return panel;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _host.DocumentSaved -= OnSaved; _lifetime.Cancel(); PersistQueue();
    }
}
