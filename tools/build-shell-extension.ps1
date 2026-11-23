param([string]$OutputDirectory = 'artifacts\extension-packages')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$msbuild = 'C:\BuildTools\MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path -LiteralPath $msbuild)) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) { $msbuild = & $vswhere -latest -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1 }
}
if (-not $msbuild -or -not (Test-Path -LiteralPath $msbuild)) { throw 'Install Visual Studio C++ Build Tools and a Windows SDK to build the native Shell extension.' }
& $msbuild (Join-Path $root 'src\InkCanvas.ShellExtension\InkCanvas.ShellExtension.vcxproj') /p:Configuration=Release /p:Platform=x64 /nologo /v:minimal
if ($LASTEXITCODE -ne 0) { throw 'Native Shell extension build failed.' }
$output = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root $OutputDirectory }
$package = Join-Path $output 'inkcanvas.shell-preview'
New-Item -ItemType Directory -Path $package -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'artifacts\shell-extension\InkCanvas.ShellExtension.dll') -Destination $package
foreach ($name in @('Install.ps1', 'Uninstall.ps1')) { Copy-Item -LiteralPath (Join-Path $root ('src\InkCanvas.ShellExtension\' + $name)) -Destination $package }
Copy-Item -LiteralPath (Join-Path $root 'docs\BOARD-LIBRARY.md') -Destination (Join-Path $package 'README.md')
$zip = Join-Path $output 'inkcanvas.shell-preview-1.0.0-win-x64.zip'
Compress-Archive -LiteralPath $package -DestinationPath $zip -Force
Write-Output $zip
