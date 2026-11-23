using System.IO.Pipes;

if (args.Length != 2 || args[0] != "--pipe" || !args[1].StartsWith("InkCanvas-Mcp-", StringComparison.Ordinal))
{
    await Console.Error.WriteLineAsync("用法 InkCanvas.McpHost --pipe InkCanvas-Mcp-<应用进程号>");
    return 2;
}
using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; lifetime.Cancel(); };
await using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
try
{
    await pipe.ConnectAsync(10000, lifetime.Token);
    var input = Console.OpenStandardInput(); var output = Console.OpenStandardOutput();
    var upstream = input.CopyToAsync(pipe, lifetime.Token);
    var downstream = pipe.CopyToAsync(output, lifetime.Token);
    await Task.WhenAny(upstream, downstream);
    lifetime.Cancel();
    try { await Task.WhenAll(upstream, downstream); } catch (OperationCanceledException) { }
    return 0;
}
catch (OperationCanceledException) { return 0; }
catch (Exception error) when (error is IOException or TimeoutException)
{ await Console.Error.WriteLineAsync("MCP 连接未建立，请确认 Ink Canvas 已运行并已开启应用工具共享。" + error.Message); return 1; }
