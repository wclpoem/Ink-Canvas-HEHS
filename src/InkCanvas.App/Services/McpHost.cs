using InkCanvas.Host.Contracts;

namespace InkCanvas.App.Services;

internal sealed record McpHost(string DataDirectory) : IMcpHost;
