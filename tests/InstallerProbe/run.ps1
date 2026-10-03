param([Parameter(Mandatory=$true)][string]$IsccPath)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$probe = Join-Path $repo ('.runtime/installer-probe-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$installed = Join-Path $probe 'bundle/Contents/Host'
$outside = Join-Path $probe 'outside'
$source = Join-Path $repo 'src/CadMcp.Host/bin/Release/net8.0-windows'
New-Item -ItemType Directory -Force -Path $installed,$outside | Out-Null
Get-ChildItem -LiteralPath $source | Copy-Item -Destination $installed -Recurse
Get-ChildItem -LiteralPath $source | Copy-Item -Destination $outside -Recurse
foreach($name in @('CadMcp.Client.exe','codex.exe')) {
    Copy-Item -LiteralPath (Join-Path $source 'CadMcp.Host.exe') -Destination (Join-Path $installed $name)
}
# This is our test apphost renamed to acad.exe; no Autodesk process is launched.
Copy-Item -LiteralPath (Join-Path $source 'CadMcp.Host.exe') -Destination (Join-Path $outside 'acad.exe')
& $IsccPath /Qp "/DProbeRoot=$probe" (Join-Path $PSScriptRoot 'setup.iss')
if($LASTEXITCODE -ne 0) { throw 'Installer process fixture did not compile' }
$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
$checks = [Collections.Generic.List[string]]::new()
function Start-TestProcess([string]$path) {
    $pipe = 'cadmcp-installer-probe-' + [Guid]::NewGuid().ToString('N')
    $process = Start-Process -FilePath $path -ArgumentList @('--broker','--broker-pipe',$pipe) -WorkingDirectory (Split-Path -Parent $path) -WindowStyle Hidden -PassThru
    $owned.Add($process)
    Start-Sleep -Milliseconds 500
    if($process.HasExited) { throw "Test process failed to start: $path" }
    return $process
}
function Invoke-TestInstaller([string]$name) {
    $log = Join-Path $probe ($name + '.log')
    $process = Start-Process -FilePath (Join-Path $probe 'InstallerProcessProbe.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="' + $log + '"')) -WindowStyle Hidden -PassThru
    $owned.Add($process)
    if(!$process.WaitForExit(30000)) { throw 'Test installer timed out' }
    return $process.ExitCode
}
try {
    $hostProcess = Start-TestProcess (Join-Path $installed 'CadMcp.Host.exe')
    $clientProcess = Start-TestProcess (Join-Path $installed 'CadMcp.Client.exe')
    $codexProcess = Start-TestProcess (Join-Path $installed 'codex.exe')
    $unrelatedProcess = Start-TestProcess (Join-Path $outside 'CadMcp.Host.exe')
    $cadGuardProcess = Start-TestProcess (Join-Path $outside 'acad.exe')
    if((Invoke-TestInstaller 'cad-guard') -eq 0) { throw 'Installation proceeded with the AutoCAD process guard active' }
    foreach($process in @($hostProcess,$clientProcess,$codexProcess,$unrelatedProcess,$cadGuardProcess)) {
        if($process.HasExited) { throw 'AutoCAD guard incorrectly terminated a process' }
    }
    $checks.Add('AutoCAD guard blocks installation before stopping any MCP process')
    $cadGuardProcess.Kill(); $cadGuardProcess.WaitForExit()
    if((Invoke-TestInstaller 'automatic-stop') -ne 0) { throw 'Automatic MCP shutdown failed; inspect automatic-stop.log' }
    foreach($process in @($hostProcess,$clientProcess,$codexProcess)) {
        if(!$process.WaitForExit(5000)) { throw 'Installed MCP process survived installation' }
    }
    $checks.Add('Installed broker, separate client and bundled Codex are stopped automatically')
    if($unrelatedProcess.HasExited) { throw 'A process outside the installation folder was terminated' }
    $checks.Add('A same-named host outside the installation folder remains running')
    if((Invoke-TestInstaller 'idempotent-stop') -ne 0) { throw 'Repeated installation without running MCP failed' }
    $checks.Add('Repeated installation without running MCP succeeds')
    @{checks=$checks.ToArray();failure=$null;directory=$probe} | ConvertTo-Json -Depth 3
} finally {
    # Only processes created by this test are eligible for cleanup.
    foreach($process in $owned) {
        try { if(!$process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null } } catch {}
        $process.Dispose()
    }
}
