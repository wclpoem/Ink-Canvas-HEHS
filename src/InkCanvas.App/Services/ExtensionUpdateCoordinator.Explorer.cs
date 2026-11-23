using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using InkCanvas.Core.Update;
using InkCanvas.Features.Update;

namespace InkCanvas.App.Services;

public sealed partial class ExtensionUpdateCoordinator
{
    private string _explorerVersion;
    private string ExplorerState => Path.Combine(_services.Store.Directory, "ExtensionUpdates", "explorer.json");
    private void ReadExplorerVersion()
    {
        try
        {
            if (File.Exists(ExplorerState)) _explorerVersion = JsonSerializer.Deserialize<string>(File.ReadAllText(ExplorerState));
            if (!Version.TryParse(_explorerVersion, out _)) _explorerVersion = null;
            if (!File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InkCanvas", "ShellExtension", "InkCanvas.ShellExtension.dll")))
                _explorerVersion = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private async Task InstallExplorerAsync(string archive, ExtensionRelease release, CancellationToken token)
    {
        string root = Path.Combine(_services.Store.Directory, "ExtensionUpdates", "explorer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var zip = ZipFile.OpenRead(archive);
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "Install.ps1", "Uninstall.ps1", "README.md", "InkCanvas.ShellExtension.dll" };
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested();
                string name = entry.FullName.Replace('\\', '/');
                if (name.EndsWith('/')) continue;
                const string prefix = "inkcanvas.shell-preview/";
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("资源管理器扩展包目录无效。");
                string relative = name[prefix.Length..];
                if (!allowed.Contains(relative) || !found.Add(relative) || entry.Length > 32 * 1024 * 1024
                    || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("资源管理器扩展包文件无效。");
                await using var input = entry.Open();
                await using var output = new FileStream(UpdatePackage.SafePath(root, relative), FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await input.CopyToAsync(output, token);
            }
            if (!found.Contains("Install.ps1") || !found.Contains("InkCanvas.ShellExtension.dll") || !found.Contains("Uninstall.ps1"))
                throw new InvalidDataException("资源管理器扩展包不完整。");
            token.ThrowIfCancellationRequested();
            SetStatus("正在为当前用户安装资源管理器预览…", UpdateStage.Installing);
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-File"); start.ArgumentList.Add(Path.Combine(root, "Install.ps1"));
            using var process = Process.Start(start) ?? throw new IOException("无法启动资源管理器扩展安装程序。");
            var outputTask = process.StandardOutput.ReadToEndAsync(); var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string error = await errorTask; await outputTask;
            if (process.ExitCode != 0) throw new IOException("资源管理器扩展安装未完成。" + error.Trim());
            _explorerVersion = release.Version;
            await File.WriteAllTextAsync(ExplorerState, JsonSerializer.Serialize(_explorerVersion));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
