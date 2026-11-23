using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using InkCanvas.Core.Versioning;

namespace InkCanvas.Core.Update;

public static class MsiPackage
{
    public const string UpgradeCode = "{71992952-A867-4E26-B2D8-91CB742DB17B}";
    public const string MarkerName = ".inkcanvas-msi";

    public static void Validate(string path, string version)
    {
        if (!AppVersion.TryParse(version, out var expected) || expected.Build != 0)
            throw new InvalidDataException("MSI 更新版本无效。");
        Check(MsiOpenDatabase(path, IntPtr.Zero, out uint database));
        try
        {
            if (Property(database, "ProductName") != "Ink Canvas"
                || !Property(database, "UpgradeCode").Equals(UpgradeCode, StringComparison.OrdinalIgnoreCase)
                || Property(database, "ProductVersion") != $"{expected.Major}.{expected.Minor}.{expected.Patch}")
                throw new InvalidDataException("MSI 的产品、升级标识或版本与 Release 不一致。");
            if (Property(database, "ALLUSERS") == "1") throw new InvalidDataException("此更新需要当前用户的 MSI 安装包。");
        }
        finally { MsiCloseHandle(database); }
    }

    private static string Property(uint database, string name)
    {
        Check(MsiDatabaseOpenView(database, $"SELECT `Value` FROM `Property` WHERE `Property` = '{name}'", out uint view));
        try
        {
            Check(MsiViewExecute(view, 0));
            uint status = MsiViewFetch(view, out uint record);
            if (status == 259) return "";
            Check(status);
            try
            {
                var value = new StringBuilder(512); uint length = 512;
                Check(MsiRecordGetString(record, 1, value, ref length));
                return value.ToString();
            }
            finally { MsiCloseHandle(record); }
        }
        finally { MsiCloseHandle(view); }
    }
    private static void Check(uint code)
    { if (code != 0) throw new InvalidDataException("MSI 数据库读取失败。", new Win32Exception((int)code)); }
    [DllImport("msi.dll", EntryPoint = "MsiOpenDatabaseW", CharSet = CharSet.Unicode)]
    private static extern uint MsiOpenDatabase(string path, IntPtr persist, out uint database);
    [DllImport("msi.dll", EntryPoint = "MsiDatabaseOpenViewW", CharSet = CharSet.Unicode)]
    private static extern uint MsiDatabaseOpenView(uint database, string query, out uint view);
    [DllImport("msi.dll")] private static extern uint MsiViewExecute(uint view, uint record);
    [DllImport("msi.dll")] private static extern uint MsiViewFetch(uint view, out uint record);
    [DllImport("msi.dll", EntryPoint = "MsiRecordGetStringW", CharSet = CharSet.Unicode)]
    private static extern uint MsiRecordGetString(uint record, uint field, StringBuilder value, ref uint length);
    [DllImport("msi.dll")] private static extern uint MsiCloseHandle(uint handle);
}
