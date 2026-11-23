using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using InkCanvas.Core.Update;

if (args.Length == 4 && args[0] == "--restart")
{
    try
    {
        int parentId = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
        long started = long.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
        string executable = Path.GetFullPath(args[3]);
        if (!Path.GetFileName(executable).Equals("InkCanvas.exe", StringComparison.OrdinalIgnoreCase)) return 2;
        try
        {
            using var parent = Process.GetProcessById(parentId);
            if (parent.StartTime.ToUniversalTime().Ticks == started)
            {
                if (!string.Equals(parent.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) return 2;
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                await parent.WaitForExitAsync(timeout.Token);
            }
        }
        catch (ArgumentException) { }
        Process.Start(new ProcessStartInfo(executable, "-m") { WorkingDirectory = Path.GetDirectoryName(executable), UseShellExecute = true });
        return 0;
    }
    catch (Exception exception)
    { InkCanvas.Core.Diagnostics.FileLog.WriteLogToFile("Restart failed: " + exception, InkCanvas.Core.Diagnostics.LogType.Error); return 1; }
}

if (args.Length != 1) return 2;
string planPath = Path.GetFullPath(args[0]), directory = Path.GetDirectoryName(planPath);
UpdatePlan plan = null;
bool parentExited = false;
try
{
    plan = JsonSerializer.Deserialize<UpdatePlan>(await File.ReadAllTextAsync(planPath)) ?? throw new InvalidDataException("Missing update plan.");
    if (plan.ParentProcessId > 0)
    {
        try
        {
            using var parent = Process.GetProcessById(plan.ParentProcessId);
            if (parent.StartTime.ToUniversalTime().Ticks == plan.ParentStartedUtcTicks
                && string.Equals(parent.MainModule?.FileName, plan.ParentExecutable, StringComparison.OrdinalIgnoreCase))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                await parent.WaitForExitAsync(timeout.Token);
            }
        }
        catch (ArgumentException) { }
    }
    parentExited = true;
    // The launcher retains this lease while alive. Hold it through replacement and rollback
    // so another Ink Canvas instance cannot reclaim the updater's working directory.
    using var cacheLease = new FileStream(Path.Combine(Path.GetDirectoryName(directory), ".lease"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    if (plan.MsiPackage is not null)
    {
        string package = Path.GetFullPath(plan.MsiPackage);
        if (!package.Equals(Path.Combine(directory, "package.msi"), StringComparison.OrdinalIgnoreCase)
            || !File.Exists(Path.Combine(plan.TargetDirectory, MsiPackage.MarkerName)))
            throw new InvalidDataException("MSI 更新计划或目标目录无效。");
        await using (var input = File.OpenRead(package))
        {
            string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input));
            if (!hash.Equals(plan.MsiSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("MSI 更新包完整性校验失败。");
        }
        MsiPackage.Validate(package, plan.Version);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string argument in new[] { "/i", package, "/qn", "/norestart", "REBOOT=ReallySuppress",
            "INSTALLFOLDER=" + Path.GetFullPath(plan.TargetDirectory), "/L*v", Path.Combine(directory, "msi-install.log") }) start.ArgumentList.Add(argument);
        using var installer = Process.Start(start) ?? throw new IOException("无法启动 Windows Installer。");
        await installer.WaitForExitAsync();
        if (installer.ExitCode is not (0 or 3010)) throw new IOException("Windows Installer 返回错误 " + installer.ExitCode + "，请查看更新目录的 msi-install.log。");
    }
    else
    {
        // File locks held by runtime teardown can outlive the parent process briefly.
        for (int attempt = 0; ; attempt++)
        {
            try { await UpdatePackage.ApplyAsync(plan, directory); break; }
            catch (IOException) when (attempt < 4) { await Task.Delay(1000); }
        }
    }
    await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new UpdateResult(true, plan.Version, "更新完成")));
    InkCanvas.Core.Diagnostics.FileLog.WriteLogToFile("Update installed " + plan.Version);
    if (plan.Restart) Process.Start(new ProcessStartInfo(Path.Combine(plan.TargetDirectory, "InkCanvas.exe")) { WorkingDirectory = plan.TargetDirectory, UseShellExecute = true });
    return 0;
}
catch (Exception exception)
{
    InkCanvas.Core.Diagnostics.FileLog.WriteLogToFile("Update failed: " + exception, InkCanvas.Core.Diagnostics.LogType.Error);
    await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new UpdateResult(false, plan?.Version, exception.ToString())));
    if (parentExited && plan?.Restart == true && !File.Exists(Path.Combine(directory, "journal.json"))
        && File.Exists(Path.Combine(plan.TargetDirectory, "InkCanvas.exe")))
        Process.Start(new ProcessStartInfo(Path.Combine(plan.TargetDirectory, "InkCanvas.exe")) { WorkingDirectory = plan.TargetDirectory, UseShellExecute = true });
    return 1;
}
