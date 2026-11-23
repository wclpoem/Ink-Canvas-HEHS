using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace InkCanvas.Platform.System;

/// <summary>Opt-in per-user registration for portable distributions. MSI owns its corresponding keys.</summary>
public static class FileAssociationService
{
    public static void Register(string executable)
    {
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable) || !Path.GetFileName(executable).Equals("InkCanvas.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("找不到 Ink Canvas 程序。", nameof(executable));
        string command = "\"" + executable + "\" \"%1\"";
        Write("Software\\Classes\\Applications\\InkCanvas.exe", "FriendlyAppName", "Ink Canvas");
        Write("Software\\Classes\\Applications\\InkCanvas.exe\\shell\\open\\command", "", command);
        foreach (var (extension, progId, title) in new[] { (".pdf", "InkCanvas.Pdf", "Ink Canvas PDF 文档"), (".icink", "InkCanvas.Board", "Ink Canvas 白板文稿") })
        {
            Write("Software\\Classes\\Applications\\InkCanvas.exe\\SupportedTypes", extension, "");
            Write("Software\\Classes\\" + progId, "", title);
            Write("Software\\Classes\\" + progId + "\\shell\\open\\command", "", command);
            Write("Software\\Classes\\" + progId + "\\DefaultIcon", "", "\"" + executable + "\",0");
            Write("Software\\Classes\\" + extension + "\\OpenWithProgids", progId, "");
            Write("Software\\InkCanvas\\Capabilities\\FileAssociations", extension, progId);
        }
        Write("Software\\InkCanvas\\Capabilities", "ApplicationName", "Ink Canvas");
        Write("Software\\InkCanvas\\Capabilities", "ApplicationDescription", "白板、屏幕批注与 PDF 阅读批注");
        Write("Software\\RegisteredApplications", "Ink Canvas", "Software\\InkCanvas\\Capabilities");
        Write("Software\\Microsoft\\Windows\\CurrentVersion\\App Paths\\InkCanvas.exe", "", executable);
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }
    private static void Write(string path, string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path);
        key.SetValue(name, value, RegistryValueKind.String);
    }
    [DllImport("shell32.dll")] private static extern void SHChangeNotify(uint events, uint flags, IntPtr item1, IntPtr item2);
}
