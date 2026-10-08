param(
    [string]$AutoCADDir = 'C:\Program Files\Autodesk\AutoCAD 2025',
    [string]$Language = 'ru-RU',
    [ValidateSet('ACAD','MAP')][string]$Product = 'ACAD'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('cmg-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $fixture | Out-Null
$output = Join-Path $fixture 'result.json'
$probe = (Resolve-Path (Join-Path $repoRoot 'tests/CadMcp.NativeProbe/bin/Release/net8.0-windows/CadMcp.NativeProbe.dll')).Path.Replace('\','/')
$profile = 'CADMCP_Test_' + [guid]::NewGuid().ToString('N')
$script = Join-Path $fixture 'probe.scr'
@('(setvar "FILEDIA" 0)', ('(setvar "LOGFILEPATH" "' + $fixture.Replace('\','/') + '")'), '(setvar "LOGFILEMODE" 1)', ('(setvar "TRUSTEDPATHS" "' + (Split-Path $probe).Replace('\','/') + '")'), '_NETLOAD', $probe, 'CADMCPPROBE') | Set-Content -LiteralPath $script -Encoding ascii
# Remember the user's profile selector; trust is changed only in our newly named test profile.
$selectors = @()
Get-ChildItem 'HKCU:\Software\Autodesk\AutoCAD\R25.0' | ForEach-Object {
    $key = Join-Path $_.PSPath 'Profiles'
    if (Test-Path $key) { $selectors += [pscustomobject]@{Path=$key;Value=(Get-ItemPropertyValue -LiteralPath $key -Name '(default)')} }
}
# /p requires an existing profile; a missing name shows a modal startup warning.
$productKey = if ($Product -eq 'MAP') { 'ACAD-8102:419' } else { 'ACAD-8101:419' }
$profilesRoot = "HKCU:\Software\Autodesk\AutoCAD\R25.0\$productKey\Profiles"
$sourceProfile = Get-ItemPropertyValue -LiteralPath $profilesRoot -Name '(default)'
$testProfileKey = Join-Path $profilesRoot $profile
if (Test-Path -LiteralPath $testProfileKey) { throw "Test profile unexpectedly already exists: $profile" }
Copy-Item -LiteralPath (Join-Path $profilesRoot $sourceProfile) -Destination $testProfileKey -Recurse
$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $AutoCADDir 'acad.exe'))
$start.UseShellExecute = $false; $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
# Match Autodesk's installed shortcut: startup dependencies may use its working directory.
$start.WorkingDirectory = Join-Path $AutoCADDir 'UserDataCache'
if (!(Test-Path -LiteralPath $start.WorkingDirectory)) { throw "AutoCAD shortcut working directory is missing: $($start.WorkingDirectory)" }
$start.Environment['CADMCP_PROBE_OUTPUT'] = $output
$start.Environment['CADMCP_PROBE_PRODUCT'] = $Product
# The probe runs cad_lisp unattended; the default policy would wait for a confirmation nobody gives.
$start.Environment['CAD_MCP_LISP_POLICY'] = 'allow'
foreach ($argument in @('/product',$Product,'/language',$Language,'/p',$profile,'/b',$script)) { $start.ArgumentList.Add($argument) }
$process = [Diagnostics.Process]::Start($start)
[IO.File]::WriteAllText($output + '.pid', $process.Id.ToString())
try {
    # Polling checks only our process/result; it never closes another AutoCAD instance.
    for ($i = 0; $i -lt 180; $i++) {
        if (Test-Path -LiteralPath $output) { break }
        if ($process.HasExited) { break }
        Start-Sleep -Milliseconds 1000
    }
    if (!(Test-Path -LiteralPath $output)) { throw "GUI probe did not return a result: $fixture" }
    $report = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
    New-Item -ItemType Directory -Force -Path (Join-Path $repoRoot 'artifacts') | Out-Null
    Copy-Item -LiteralPath $output -Destination (Join-Path $repoRoot 'artifacts/gui-probe-latest.json')
    Copy-Item -LiteralPath $output -Destination (Join-Path $repoRoot "artifacts/gui-probe-$Product.json")
    $report
    if ($report.failure) { throw $report.failure }
} finally {
    if (!$process.HasExited -and !$process.WaitForExit(10000)) { $process.Kill(); $process.WaitForExit() }
    foreach ($selector in $selectors) {
        if ((Get-ItemPropertyValue -LiteralPath $selector.Path -Name '(default)') -eq $profile) {
            Set-ItemProperty -LiteralPath $selector.Path -Name '(default)' -Value $selector.Value
        }
    }
    # Remove only this invocation's unique profile after the owned process has exited.
    if ($process.HasExited -and (Split-Path $testProfileKey -Parent) -eq $profilesRoot -and (Split-Path $testProfileKey -Leaf) -eq $profile) {
        Remove-Item -LiteralPath $testProfileKey -Recurse -Force
    }
}
