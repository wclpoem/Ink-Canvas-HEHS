using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace InkCanvas.Features.Ppt;

public sealed record PptPreviewRequest(PptPresentationInfo Presentation, int Index);
public sealed record PptPreviewResponse(PptSlidePreviewPage Page, string Error = null);

/// <summary>Owns a disposable preview process so a stalled Office export can be abandoned.</summary>
internal sealed class PptPreviewProcess : IPptSlidePreviewTransport
{
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private Process _process;
    private NamedPipeServerStream _pipe;
    private StreamReader _reader;
    private StreamWriter _writer;
    private string _directory;
    private volatile bool _disposed;

    public async Task<PptSlidePreviewPage> RenderAsync(PptPresentationInfo presentation, int index, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        token = request.Token;
        await _requests.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process is null || _process.HasExited) await StartAsync(token).ConfigureAwait(false);
            await _writer.WriteLineAsync(JsonSerializer.Serialize(new PptPreviewRequest(presentation, index)).AsMemory(), token).ConfigureAwait(false);
            await _writer.FlushAsync(token).ConfigureAwait(false);
            string line = await _reader.ReadLineAsync(token).ConfigureAwait(false);
            if (line is null || line.Length > 4 * 1024 * 1024) throw new IOException("预览进程连接已中断。");
            var response = JsonSerializer.Deserialize<PptPreviewResponse>(line);
            if (response?.Page is null) throw new InvalidOperationException(response?.Error ?? "此页暂时无法生成预览。");
            return response.Page;
        }
        catch
        {
            Stop();
            throw;
        }
        finally { _requests.Release(); }
    }

    private async Task StartAsync(CancellationToken token)
    {
        Stop();
        string name = "inkcanvas-ppt-" + Guid.NewGuid().ToString("N");
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _directory = Directory.CreateTempSubdirectory("inkcanvas-ppt-preview-").FullName;
            _pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var start = new ProcessStartInfo(Environment.ProcessPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            start.ArgumentList.Add("--pptpreviewworker=" + name);
            start.ArgumentList.Add("--pptpreviewparent=" + Environment.ProcessId);
            start.ArgumentList.Add("--pptpreviewdirectory=" + _directory);
            _process = Process.Start(start) ?? throw new IOException("无法启动预览进程。");
        }
        await _pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _reader = new StreamReader(_pipe, leaveOpen: true);
            _writer = new StreamWriter(_pipe, leaveOpen: true);
        }
    }

    private void Stop()
    {
        lock (_sync)
        {
            // Closing the pipe first unblocks pending reads and prevents writer disposal from flushing to a stalled worker.
            _pipe?.Dispose(); _pipe = null;
            _reader?.Dispose(); _reader = null;
            try { _writer?.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
            _writer = null;
            if (_process is not null)
            {
                try { if (!_process.HasExited) _process.Kill(); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                _process.Dispose(); _process = null;
            }
            if (_directory is null) return;
            string directory = _directory;
            _directory = null;
            _ = Task.Run(async () =>
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        string image = Path.Combine(directory, "slide.png");
                        if (File.Exists(image)) File.Delete(image);
                        Directory.Delete(directory, recursive: false);
                        return;
                    }
                    catch (IOException) { await Task.Delay(200); }
                    catch (UnauthorizedAccessException) { return; }
                }
            });
        }
    }

    public void Dispose() { _disposed = true; _lifetime.Cancel(); Stop(); }
}
