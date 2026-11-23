param(
    [string]$Version,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src\InkCanvas.App\InkCanvas.App.csproj'
[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$projectVersion = $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = $projectVersion }
if ($Version -ne $projectVersion) { throw 'Release version must match the application project version.' }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = 'artifacts\release-' + $Version }
$outputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory)) }
$app = Join-Path $outputRoot 'InkCanvas'
if (Test-Path -LiteralPath $app) { throw 'Use a fresh release output directory so older build files cannot enter the release.' }
New-Item -ItemType Directory -Path $app -Force | Out-Null
& dotnet publish $projectPath -c Release -p:Platform=x64 -m:1 -p:BuildInParallel=false -o $app --nologo
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
$modules = @('InkCanvas.Modules.Library', 'InkCanvas.Modules.AiAssistant', 'InkCanvas.Modules.AnalyticGeometry', 'InkCanvas.Modules.Pdf', 'InkCanvas.Modules.ScreenTracking', 'InkCanvas.Modules.TimeMachine', 'InkCanvas.Modules.CloudSync', 'InkCanvas.Modules.Mcp')
foreach ($module in $modules) {
    & (Join-Path $PSScriptRoot 'pack-extension.ps1') -Project (Join-Path $repoRoot ('src\' + $module + '\' + $module + '.csproj')) -Kind Module -OutputDirectory $outputRoot
    if ($LASTEXITCODE -ne 0) { throw ('Module publish failed ' + $module) }
}
& (Join-Path $PSScriptRoot 'build-shell-extension.ps1') -OutputDirectory $outputRoot
foreach ($plugin in @('src\InkCanvas.Plugins.LegacyImport\InkCanvas.Plugins.LegacyImport.csproj', 'samples\InkCanvas.SamplePlugin\InkCanvas.SamplePlugin.csproj')) {
    & (Join-Path $PSScriptRoot 'pack-extension.ps1') -Project (Join-Path $repoRoot $plugin) -Kind Plugin -OutputDirectory $outputRoot
    if ($LASTEXITCODE -ne 0) { throw ('Plugin publish failed ' + $plugin) }
}
$docs = Join-Path $app 'Docs'
New-Item -ItemType Directory -Path $docs -Force | Out-Null
foreach ($name in @('BOARD-LIBRARY.md', 'AI-ASSISTANT.md', 'ANALYTIC-GEOMETRY.md', 'PDF.md', 'EXTENSIONS.md', 'SCREEN-TRACKING.md', 'UPDATES.md', 'CLOUD-SYNC.md', 'INSTALLATION.md', 'MCP.md', 'teaching-loop-user-guide.md', 'timemachine-user-guide.md', 'ai-magic-lasso.md')) { Copy-Item -LiteralPath (Join-Path $repoRoot ('docs\' + $name)) -Destination $docs }
$archive = Join-Path $outputRoot ('InkCanvas-WinUI3-' + $Version + '-win-x64.zip')
Compress-Archive -LiteralPath $app -DestinationPath $archive
& (Join-Path $PSScriptRoot 'build-msi.ps1') -PayloadDirectory $app -Version $Version -OutputDirectory $outputRoot
$lines = Get-ChildItem -LiteralPath $outputRoot -File | Where-Object { $_.Extension -in @('.zip', '.msi') } | Sort-Object Name | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name }
[IO.File]::WriteAllLines((Join-Path $outputRoot 'SHA256SUMS.txt'), $lines, [Text.UTF8Encoding]::new($false))
Write-Output $archive
& (Join-Path $PSScriptRoot 'prune-builds.ps1')
