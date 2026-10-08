param([string]$AutoCADDir = 'C:\Program Files\Autodesk\AutoCAD 2025', [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('cmc-' + [guid]::NewGuid().ToString('N').Substring(0,8)) }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$probe = (Resolve-Path -LiteralPath (Join-Path $repoRoot 'tests/CadMcp.CoreProbe/bin/Release/net8.0-windows/CadMcp.CoreProbe.dll')).Path.Replace('\','/')
$trusted = (Split-Path -Parent $probe).Replace('\','/')
$script = Join-Path $OutputDirectory 'probe.scr'
$core = [Reflection.Assembly]::LoadFrom((Join-Path $repoRoot 'src/CadMcp.Core/bin/Release/net8.0/CadMcp.Core.dll'))
$wrapper = $core.GetType('CadMcp.Core.LispScript').GetMethod('Wrap').Invoke($null, @('core_lisp')).Trim()
# Core Console does not pump SendStringToExecute between startup-script lines.
# Execute the same production wrapper explicitly; GUI queue delivery is tested separately.
@(('(setvar "TRUSTEDPATHS" "' + $trusted + '")'),'_NETLOAD',$probe,'CADMCPCOREPROBE','CADMCPCORELISP',$wrapper,'CADMCPCORELISPVERIFY','_.QUIT','_Yes') | Set-Content -LiteralPath $script -Encoding ascii
$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $AutoCADDir 'accoreconsole.exe'))
$start.UseShellExecute = $false; $start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
$start.StandardOutputEncoding = [Text.Encoding]::Unicode; $start.StandardErrorEncoding = [Text.Encoding]::Unicode
$start.WorkingDirectory = $OutputDirectory
foreach ($argument in @('/isolate', ('cadmcp-' + [guid]::NewGuid().ToString('N')), (Join-Path $OutputDirectory 'profile'), '/s', $script)) { $start.ArgumentList.Add($argument) }
$result = Join-Path $OutputDirectory 'result.json'
$start.Environment['CADMCP_PROBE_OUTPUT'] = $result
# The probe runs cad_lisp unattended; the default policy would wait for a confirmation nobody gives.
$start.Environment['CAD_MCP_LISP_POLICY'] = 'allow'
$process = [Diagnostics.Process]::Start($start)
$stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
try { if (!$process.WaitForExit(55000)) { $process.Kill(); $process.WaitForExit() } }
finally { $stdout.Result | Set-Content -LiteralPath (Join-Path $OutputDirectory 'stdout.log'); $stderr.Result | Set-Content -LiteralPath (Join-Path $OutputDirectory 'stderr.log') }
if (!(Test-Path -LiteralPath $result)) { throw "AutoCAD did not return a probe result: $OutputDirectory" }
$report = Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Force -Path (Join-Path $repoRoot 'artifacts') | Out-Null
Copy-Item -LiteralPath $result -Destination (Join-Path $repoRoot 'artifacts/core-probe-latest.json')
$report
if ($report.failure) { throw $report.failure }
if (!(Test-Path -LiteralPath ($result + '.lisp'))) { throw 'Core Console did not return the LISP result' }
$lisp = Get-Content -LiteralPath ($result + '.lisp') -Raw | ConvertFrom-Json
Copy-Item -LiteralPath ($result + '.lisp') -Destination (Join-Path $repoRoot 'artifacts/core-lisp-latest.json')
$lisp
if ($lisp.failure) { throw $lisp.failure }
