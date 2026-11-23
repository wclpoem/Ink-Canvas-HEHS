param(
    [Parameter(Mandatory)][string]$ApplicationPath,
    [string]$OutputDirectory = 'artifacts\pdf-lifetime-checks',
    [ValidateRange(1, 100)][int]$Runs = 10
)
$ErrorActionPreference = 'Stop'
$application = (Resolve-Path -LiteralPath $ApplicationPath).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$results = @()
for ($run = 1; $run -le $Runs; $run++) {
    $directory = Join-Path $output ('run-' + $run + '-' + [Guid]::NewGuid().ToString('N'))
    $process = Start-Process -FilePath $application -ArgumentList ('"--pdflifetimecheck=' + $directory + '"') -WindowStyle Hidden -PassThru
    try {
        if (-not $process.WaitForExit(45000)) {
            # This handle belongs to the diagnostic process created immediately above.
            $process.Kill()
            throw ('PDF lifetime check timed out in run ' + $run)
        }
        $exitCode = $process.ExitCode
    } finally { $process.Dispose() }

    $report = Join-Path $directory 'pdf-lifetime.json'
    $checks = if (Test-Path -LiteralPath $report) { @(Get-Content -LiteralPath $report -Raw | ConvertFrom-Json) } else { @() }
    $failed = @($checks | Where-Object { $_.passed -ne $true })
    $result = [pscustomobject]@{ Run = $run; ExitCode = $exitCode; Checks = $checks.Count; Failed = $failed.Count; Report = $report }
    $results += $result
    $results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'process-exits.json') -Encoding utf8
    $result | ConvertTo-Json -Compress
    if ($exitCode -ne 0 -or $checks.Count -eq 0 -or $failed.Count -gt 0) {
        throw ('PDF lifetime check failed in run ' + $run + '. See ' + $directory)
    }
}
