$ErrorActionPreference = 'Stop'
$classes = 'HKCU:\Software\Classes'
$thumbnail = '{83AD3CDD-699A-4C40-BF31-0B6F564B8A91}'
$preview = '{179CDDB8-995A-40B5-81F9-1EDECF07245C}'
$directory = Join-Path $env:LOCALAPPDATA 'InkCanvas\ShellExtension'
$backupPath = Join-Path $directory 'previous-handlers.json'
$backup = if (Test-Path -LiteralPath $backupPath) { Get-Content -LiteralPath $backupPath -Raw | ConvertFrom-Json } else { $null }
foreach ($pair in @(@("$classes\.icink\shellex\{E357FCCD-A995-4576-B01F-234630154E96}", $thumbnail), @("$classes\.icink\shellex\{8895B1C6-B41F-4C1C-A562-0D564250836F}", $preview))) {
    $key = $pair[0]
    if ((Test-Path -LiteralPath $key) -and (Get-Item -LiteralPath $key).GetValue('') -eq $pair[1]) {
        $previous = if ($backup) { $backup.PSObject.Properties[$key].Value } else { $null }
        if ($previous) { Set-Item -LiteralPath $key -Value $previous } else { Remove-Item -LiteralPath $key }
    }
}
foreach ($id in @($thumbnail, $preview)) {
    $key = "$classes\CLSID\$id"
    if (Test-Path -LiteralPath $key) { Remove-Item -LiteralPath $key -Recurse }
}
$handlers = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\PreviewHandlers'
if (Test-Path -LiteralPath $handlers) { Remove-ItemProperty -LiteralPath $handlers -Name $preview -ErrorAction SilentlyContinue }
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class InkCanvasShellRemoveNotify { [DllImport("shell32.dll")] public static extern void SHChangeNotify(uint e, uint f, IntPtr a, IntPtr b); }'
[InkCanvasShellRemoveNotify]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
# Delete only files installed by this package. Explorer may release the DLL later.
foreach ($name in @('InkCanvas.ShellExtension.dll', 'previous-handlers.json')) {
    $file = Join-Path $directory $name
    if (Test-Path -LiteralPath $file) { try { Remove-Item -LiteralPath $file } catch [IO.IOException], [UnauthorizedAccessException] { Write-Output ('资源管理器尚在使用 ' + $file + '，注销 Windows 后可移除。') } }
}
Write-Output '已卸载当前用户的白板缩略图和预览扩展。'
