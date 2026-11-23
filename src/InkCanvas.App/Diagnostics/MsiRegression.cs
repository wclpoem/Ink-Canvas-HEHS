using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using InkCanvas.Core.Update;
using InkCanvas.Features.Update;
using Microsoft.UI.Xaml;

namespace InkCanvas.App.Diagnostics;

internal static class MsiRegression
{
    public static async Task Run(string directory, string package)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        try
        {
            MsiPackage.Validate(package, "3.10.0"); Check("MSI product upgrade identity and release version match", true);
            bool rejected = false;
            try { MsiPackage.Validate(package, "3.9.0"); }
            catch (InvalidDataException) { rejected = true; }
            Check("MSI wrong release version is rejected", rejected);
            string hash;
            await using (var input = File.OpenRead(package)) hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input));
            var release = new ReleaseInfo("v3.10.0", new(3, 10, 0, 0), "MSI", "", "",
                "https://github.com/QiXinia/Ink-Canvas-HEHS/releases/download/v3.10.0/InkCanvas-WinUI3-3.10.0-win-x64.msi", new FileInfo(package).Length)
                { AssetName = "InkCanvas-WinUI3-3.10.0-win-x64.msi", Sha256 = hash };
            string target = Path.Combine(directory, "installed-target"); Directory.CreateDirectory(target);
            await File.WriteAllTextAsync(Path.Combine(target, MsiPackage.MarkerName), "diagnostic target");
            using var service = new UpdateService(cache: new UpdateCache(Path.Combine(directory, "cache")), preferMsi: true);
            string planPath = await service.PrepareAsync(release, package, target, restart: false);
            var plan = JsonSerializer.Deserialize<UpdatePlan>(await File.ReadAllTextAsync(planPath));
            Check("MSI update stages a verified package instead of ZIP replacement", plan.MsiSha256 == hash && File.Exists(plan.MsiPackage) && plan.Files.Length == 0);
            await File.WriteAllTextAsync(plan.MsiPackage, "tampered");
            plan = plan with { ParentProcessId = 0, Restart = false };
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan));
            using var helper = UpdateService.LaunchUpdate(planPath);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await helper.WaitForExitAsync(timeout.Token);
            var result = JsonSerializer.Deserialize<UpdateResult>(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(planPath), "result.json")));
            Check("external update host rejects tampered MSI before invoking installer", helper.ExitCode != 0 && !result.Success && result.Message.Contains("完整性校验"));
        }
        catch (Exception error) { checks.Add(new { check = "MSI regression exception", passed = false, error = error.ToString() }); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "msi-check.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Exit();
        }
        void Check(string check, bool passed) => checks.Add(new { check, passed });
    }
}
