using System.IO.Pipes;

namespace InkCanvas.Modules.Mcp;

public sealed partial class McpService
{
    private CancellationTokenSource _serverLifetime;
    private readonly HashSet<NamedPipeServerStream> _pipes = new();
    private readonly object _pipeGate = new();

    private void ConfigureProjectServer(bool enabled)
    {
        StopProjectServer();
        if (!enabled || _disposed) return;
        _serverLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ = AcceptConnectionsAsync(_serverLifetime.Token);
    }
    private async Task AcceptConnectionsAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 16, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                lock (_pipeGate) _pipes.Add(pipe);
                try { await pipe.WaitForConnectionAsync(token).ConfigureAwait(false); }
                catch { lock (_pipeGate) _pipes.Remove(pipe); pipe.Dispose(); throw; }
                _ = ServeConnectionAsync(pipe, token);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { }
        catch (Exception error) { _configurationError = "应用 MCP 服务未启动。" + error.Message; NotifyChanged(); }
    }
    private async Task ServeConnectionAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        try
        {
            await using var server = McpProjectServer.Create(pipe, pipe, _registry);
            await server.RunAsync(token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
        catch (Exception error) { _configurationError = "MCP 客户端连接已结束。" + error.Message; NotifyChanged(); }
        finally { lock (_pipeGate) _pipes.Remove(pipe); pipe.Dispose(); }
    }
    private void StopProjectServer()
    {
        _serverLifetime?.Cancel(); _serverLifetime?.Dispose(); _serverLifetime = null;
        lock (_pipeGate)
        {
            foreach (var pipe in _pipes) pipe.Dispose();
            _pipes.Clear();
        }
    }
}
