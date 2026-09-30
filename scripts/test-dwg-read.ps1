param(
    [Parameter(Mandatory=$true)][string]$Drawing,
    [string]$AutoCADDir = 'C:\Program Files\Autodesk\AutoCAD 2025'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$source = (Resolve-Path -LiteralPath $Drawing).ProviderPath
if ([IO.Path]::GetExtension($source) -ne '.dwg') { throw 'Expected a DWG file' }
# AutoCAD's /isolate profile copy uses paths with a legacy length limit.
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('cmr-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $fixture | Out-Null
$copy = Join-Path $fixture 'input.dwg'
Copy-Item -LiteralPath $source -Destination $copy
$probe = (Resolve-Path -LiteralPath (Join-Path $repoRoot 'tests/CadMcp.CoreProbe/bin/Release/net8.0-windows/CadMcp.CoreProbe.dll')).ProviderPath.Replace('\','/')
$trusted = (Split-Path -Parent $probe).Replace('\','/')
$script = Join-Path $fixture 'probe.scr'
@('(setvar "FILEDIA" 0)', ('(setvar "TRUSTEDPATHS" "' + $trusted + '")'), '_NETLOAD', $probe, 'CADMCPREADPROBE') |
    Set-Content -LiteralPath $script -Encoding ascii
$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $AutoCADDir 'accoreconsole.exe'))
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.StandardOutputEncoding = [Text.Encoding]::Unicode
$start.StandardErrorEncoding = [Text.Encoding]::Unicode
$start.WorkingDirectory = $fixture
foreach ($argument in @('/isolate', ('cadmcp-read-' + [guid]::NewGuid().ToString('N')), (Join-Path $fixture 'profile'), '/i', $copy, '/s', $script)) { $start.ArgumentList.Add($argument) }
$result = Join-Path $fixture 'result.json'
$start.Environment['CADMCP_PROBE_OUTPUT'] = $result
$process = [Diagnostics.Process]::Start($start)
$stdout = $process.StandardOutput.ReadToEndAsync()
$stderr = $process.StandardError.ReadToEndAsync()
try {
    for ($i = 0; $i -lt 60; $i++) {
        if (Test-Path -LiteralPath $result) { break }
        if ($process.HasExited) { break }
        Start-Sleep -Milliseconds 1000
    }
} finally {
    # This is only the Core Console process started for this disposable copy.
    if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $stdout.Result | Set-Content -LiteralPath (Join-Path $fixture 'stdout.log')
    $stderr.Result | Set-Content -LiteralPath (Join-Path $fixture 'stderr.log')
}
if (!(Test-Path -LiteralPath $result)) { throw "Read probe did not return a result: $fixture" }
$report = Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
$report
if ($report.failure) { throw $report.failure }
if (@($report.context.status,$report.catalog.status,$report.search.status,$report.snapshot.status) | Where-Object { $_ -notin @('completed','partial') }) {
    throw "Read probe returned a failed operation: $result"
}
if ($report.actual_revision -ne $report.context.revision -or $report.dbmod_before -ne $report.dbmod_after) {
    throw "Read probe changed the DWG revision or DBMOD: $result"
}
