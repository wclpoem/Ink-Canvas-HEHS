param(
    [Parameter(Mandatory)][string]$PayloadDirectory,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$WixPath = (Join-Path $env:LOCALAPPDATA 'InkCanvas\BuildTools\wix\wix.exe')
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$payload = [IO.Path]::GetFullPath($PayloadDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath (Join-Path $payload 'InkCanvas.exe'))) { throw 'Payload is missing InkCanvas.exe.' }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'MSI requires a three-part version.' }
if (-not (Test-Path -LiteralPath $WixPath)) { throw 'Install WiX 5.0.2 using the instructions in docs/INSTALLATION.md.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$stage = Join-Path $output 'msi-source'
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$namespace = 'http://wixtoolset.org/schemas/v4/wxs'
$document = [Xml.XmlDocument]::new()
$wix = $document.CreateElement('Wix', $namespace); [void]$document.AppendChild($wix)
$fragment = $document.CreateElement('Fragment', $namespace); [void]$wix.AppendChild($fragment)
function Element($parent, $name, $attributes) {
    $element = $document.CreateElement($name, $namespace)
    foreach ($entry in $attributes.GetEnumerator()) { $element.SetAttribute($entry.Key, [string]$entry.Value) }
    [void]$parent.AppendChild($element)
    return $element
}
function StableId([string]$name) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($name.ToLowerInvariant())
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).Substring(0, 32)
}
$directoryRef = Element $fragment 'DirectoryRef' @{ Id = 'INSTALLFOLDER' }
$directories = @{ '' = $directoryRef }
$directoryIds = @{ '' = 'INSTALLFOLDER' }
$group = Element $fragment 'ComponentGroup' @{ Id = 'Payload' }
$cleanupDirectories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName) {
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Installer payload must contain regular files.' }
    $relative = [IO.Path]::GetRelativePath($payload, $file.FullName)
    $folder = [IO.Path]::GetDirectoryName($relative)
    $current = ''
    foreach ($segment in @($folder -split '[\\/]' | Where-Object { $_ })) {
        $parent = $current
        $current = if ($current) { $current + '\' + $segment } else { $segment }
        if (-not $directories.ContainsKey($current)) {
            $id = 'D' + (StableId $current)
            $directories[$current] = Element $directories[$parent] 'Directory' @{ Id = $id; Name = $segment }
            $directoryIds[$current] = $id
        }
    }
    $hash = StableId $relative
    $id = if ($relative -eq 'InkCanvas.exe') { 'MainExecutable' } else { 'F' + $hash }
    $component = Element $group 'Component' @{ Id = 'C' + $hash; Directory = $directoryIds[$folder]; Guid = ([Guid]::new($hash)).ToString('B') }
    [void](Element $component 'File' @{ Id = $id; Source = $file.FullName; Name = $file.Name })
    [void](Element $component 'RegistryValue' @{ Root = 'HKCU'; Key = 'Software\InkCanvas\InstallerComponents'; Name = $hash; Value = "[#$id]"; Type = 'string'; KeyPath = 'yes' })
    if ($cleanupDirectories.Add($folder)) { [void](Element $component 'RemoveFolder' @{ Id = 'R' + (StableId $folder); Directory = $directoryIds[$folder]; On = 'uninstall' }) }
}
$generated = Join-Path $stage 'Payload.wxs'; $document.Save($generated)
$license = Join-Path $stage 'License.rtf'
$licenseText = [IO.File]::ReadAllText((Join-Path $repoRoot 'LICENSE'))
$escaped = $licenseText.Replace('\', '\\').Replace('{', '\{').Replace('}', '\}').Replace("`r", '').Replace("`n", '\par ')
[IO.File]::WriteAllText($license, '{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\f0\fs18 ' + $escaped + '}', [Text.Encoding]::ASCII)
$installer = Join-Path $output ('InkCanvas-WinUI3-' + $Version + '-win-x64.msi')
& $WixPath build (Join-Path $repoRoot 'installer\InkCanvas.wxs') $generated -arch x64 -culture zh-CN `
    -ext WixToolset.UI.wixext -ext WixToolset.Netfx.wixext -d ('Version=' + $Version) `
    -d ('PayloadDir=' + $payload) -d ('InstallerDir=' + (Join-Path $repoRoot 'installer')) -d ('LicenseRtf=' + $license) -o $installer
if ($LASTEXITCODE -ne 0) { throw 'MSI build failed.' }
Write-Output $installer
