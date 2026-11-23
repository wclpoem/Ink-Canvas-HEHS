[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts'))
if (-not (Test-Path -LiteralPath $artifactsRoot)) { return }
$rootPrefix = $artifactsRoot.TrimEnd('\') + '\'
# A running build can retain locked DLLs while other resources are already deleted.
# Check before deleting any part of a build and retry it on the next cleanup.
$runningExecutables = @(Get-CimInstance Win32_Process -Filter "Name = 'InkCanvas.exe'" -ErrorAction Stop |
    Where-Object ExecutablePath | ForEach-Object { [IO.Path]::GetFullPath($_.ExecutablePath) })
$builds = @(Get-ChildItem -LiteralPath $artifactsRoot -Directory | Where-Object {
    -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint)
} | ForEach-Object {
    $executables = @(Get-ChildItem -LiteralPath $_.FullName -Filter InkCanvas.exe -File -Recurse)
    if ($executables.Count -gt 0) {
        $root = $_.FullName
        $main = $executables | Where-Object {
            $_.FullName -eq (Join-Path $root 'InkCanvas.exe') -or
            $_.FullName -eq (Join-Path $root 'InkCanvas\InkCanvas.exe')
        } | Select-Object -First 1
        [pscustomobject]@{ Path = $root; Main = $main }
    }
})
$retained = @($builds | Where-Object Main | Sort-Object { $_.Main.LastWriteTimeUtc } -Descending | Select-Object -First 3)
$retainedPaths = @($retained.Path)
foreach ($build in $builds) {
    if ($build.Path -in $retainedPaths) { continue }
    $resolved = [IO.Path]::GetFullPath($build.Path)
    if (-not $resolved.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Build path is outside artifacts: $resolved"
    }
    if (@($runningExecutables | Where-Object { $_.StartsWith($resolved + '\', [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) {
        Write-Output ('Deferred running build ' + $resolved)
        continue
    }
    if ($PSCmdlet.ShouldProcess($resolved, 'Remove historical build')) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
# Old release archives and extension packages outside the retained builds are also build output.
Get-ChildItem -LiteralPath $artifactsRoot -File -Recurse | Where-Object {
    $_.Name -like 'InkCanvas-*.zip' -or $_.Name -like 'InkCanvas-*.msi' -or $_.Name -like 'inkcanvas.*.zip' -or $_.Name -like 'sample.guide-lines-*.zip'
} | ForEach-Object {
    $archive = $_.FullName
    $insideRetained = @($retainedPaths | Where-Object {
        $archive.StartsWith($_ + '\', [StringComparison]::OrdinalIgnoreCase)
    }).Count -gt 0
    if (-not $insideRetained -and $PSCmdlet.ShouldProcess($archive, 'Remove historical build archive')) {
        Remove-Item -LiteralPath $archive -Force
    }
}
$retained | ForEach-Object { Write-Output ('Retained build ' + $_.Path) }
