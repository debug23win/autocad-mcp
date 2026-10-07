# Runs the automated tests. Run scripts/build.ps1 first; the tests start the built CadMcp.Host and a fake CLI.
# No AutoCAD, real Codex/Claude CLI or model is started. Results: artifacts/test-results/*.trx
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $repoRoot '.runtime/dotnet'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    dotnet test tests/CadMcp.Tests/CadMcp.Tests.csproj --no-restore -c Release -m:1 /nodeReuse:false --logger 'trx;LogFileName=CadMcp.Tests.trx' --results-directory artifacts/test-results
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
} finally { Pop-Location }
