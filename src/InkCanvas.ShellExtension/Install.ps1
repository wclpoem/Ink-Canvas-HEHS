$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitProcess) { throw 'Please run this installer in 64-bit PowerShell.' }
$destination = Join-Path $env:LOCALAPPDATA 'InkCanvas\ShellExtension'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$source = Join-Path $PSScriptRoot 'InkCanvas.ShellExtension.dll'
$installed = Join-Path $destination 'InkCanvas.ShellExtension.dll'
if ([IO.Path]::GetFullPath($source) -ne [IO.Path]::GetFullPath($installed)) { Copy-Item -LiteralPath $source -Destination $installed -Force }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall.ps1') -Destination $destination -Force
$classes = 'HKCU:\Software\Classes'
$thumbnail = '{83AD3CDD-699A-4C40-BF31-0B6F564B8A91}'
$preview = '{179CDDB8-995A-40B5-81F9-1EDECF07245C}'
$thumbKey = "$classes\.icink\shellex\{E357FCCD-A995-4576-B01F-234630154E96}"
$previewKey = "$classes\.icink\shellex\{8895B1C6-B41F-4C1C-A562-0D564250836F}"
$backup = Join-Path $destination 'previous-handlers.json'
if (-not (Test-Path -LiteralPath $backup)) {
    $previous = @{}
    foreach ($key in @($thumbKey, $previewKey)) {
        $previous[$key] = if (Test-Path -LiteralPath $key) { (Get-Item -LiteralPath $key).GetValue('') } else { $null }
    }
    $previous | ConvertTo-Json | Set-Content -LiteralPath $backup -Encoding utf8
}
foreach ($id in @($thumbnail, $preview)) {
    $key = "$classes\CLSID\$id"
    New-Item -Path "$key\InprocServer32" -Force | Out-Null
    Set-Item -LiteralPath $key -Value 'Ink Canvas document preview'
    Set-Item -LiteralPath "$key\InprocServer32" -Value $installed
    New-ItemProperty -LiteralPath "$key\InprocServer32" -Name ThreadingModel -Value Apartment -Force | Out-Null
}
# Windows' standard low-integrity preview surrogate. No DisableLowILProcessIsolation override.
New-ItemProperty -LiteralPath "$classes\CLSID\$preview" -Name AppID -Value '{6D2B5079-2F0B-48DD-AB7F-97CEC514D30B}' -Force | Out-Null
foreach ($pair in @(@($thumbKey, $thumbnail), @($previewKey, $preview))) {
    New-Item -Path $pair[0] -Force | Out-Null
    Set-Item -LiteralPath $pair[0] -Value $pair[1]
}
$handlers = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\PreviewHandlers'
New-Item -Path $handlers -Force | Out-Null
New-ItemProperty -LiteralPath $handlers -Name $preview -Value 'Ink Canvas 白板预览' -Force | Out-Null
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class InkCanvasShellNotify { [DllImport("shell32.dll")] public static extern void SHChangeNotify(uint e, uint f, IntPtr a, IntPtr b); }'
[InkCanvasShellNotify]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
Write-Output ('已为当前用户安装白板缩略图和预览。卸载脚本位于 ' + (Join-Path $destination 'Uninstall.ps1'))
