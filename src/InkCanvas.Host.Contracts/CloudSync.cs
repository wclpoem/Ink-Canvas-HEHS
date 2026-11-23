namespace InkCanvas.Host.Contracts;

public interface ICloudSyncHost
{
    string DataDirectory { get; }
    string DefaultDirectory { get; }
    event Action<string> BoardSaved;
    event Action PreferencesSaved;
    Task<string> SaveSnapshotAsync(CancellationToken token);
    string ExportPreferences();
    void ImportPreferences(string json);
}

public interface ICloudSyncService
{
    Task SyncAsync();
}
