param(
    [Parameter(Mandatory)][string]$Project,
    [ValidateSet('Plugin','Module')][string]$Kind = 'Plugin',
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = 'artifacts\extension-packages'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = if ([IO.Path]::IsPathRooted($Project)) { [IO.Path]::GetFullPath($Project) } else { [IO.Path]::GetFullPath((Join-Path $repoRoot $Project)) }
$projectDirectory = Split-Path -Parent $projectPath
$manifestName = if ($Kind -eq 'Module') { 'module.json' } else { 'plugin.json' }
$manifestPath = Join-Path $projectDirectory $manifestName
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$parsedVersion = $null
if ($manifest.Id -notmatch '^[a-z0-9][a-z0-9.-]{2,63}$' -or $manifest.ApiVersion -ne 1 -or -not [Version]::TryParse($manifest.Version, [ref]$parsedVersion)) { throw 'Invalid extension ID, version or API version.' }
if ([IO.Path]::IsPathRooted($manifest.EntryAssembly) -or $manifest.EntryAssembly -notlike '*.dll') { throw 'Invalid extension entry assembly.' }
$outputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory)) }
$stageRoot = Join-Path $outputRoot ('stage-' + [Guid]::NewGuid().ToString('N'))
$published = Join-Path $stageRoot 'published'
$package = Join-Path $stageRoot $manifest.Id
New-Item -ItemType Directory -Path $published,$package -Force | Out-Null
try {
    & dotnet publish $projectPath -c $Configuration -p:Platform=x64 -m:1 -p:BuildInParallel=false -o $published --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Extension publish failed.' }
    if ($Kind -eq 'Module') {
        $files = @($manifestName, $manifest.EntryAssembly, ([IO.Path]::GetFileNameWithoutExtension($manifest.EntryAssembly) + '.deps.json')) + @($manifest.PackageFiles)
        foreach ($relative in $files) {
            $source = [IO.Path]::GetFullPath((Join-Path $published $relative))
            if (-not $source.StartsWith($published + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package entry escapes its root.' }
            $destination = Join-Path $package $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath $source -Destination $destination
        }
    } else {
        Get-ChildItem -LiteralPath $published | Copy-Item -Destination $package -Recurse
    }
    $zip = Join-Path $outputRoot ($manifest.Id + '-' + $manifest.Version + '.zip')
    Compress-Archive -LiteralPath $package -DestinationPath $zip -Force
    Write-Output $zip
} finally {
    $resolvedStage = [IO.Path]::GetFullPath($stageRoot)
    $stagePrefix = $outputRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedStage.StartsWith($stagePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Stage path escapes its output directory.' }
    Remove-Item -LiteralPath $resolvedStage -Recurse -Force
}
