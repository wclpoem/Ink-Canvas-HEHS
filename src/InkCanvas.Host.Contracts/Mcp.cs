namespace InkCanvas.Host.Contracts;

public interface IMcpHost
{
    string DataDirectory { get; }
}

public interface IMcpService
{
    string PipeName { get; }
    Task RefreshAsync(CancellationToken cancellationToken = default);
}
