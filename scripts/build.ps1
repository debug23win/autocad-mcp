param([string]$AutoCADDir = 'C:\Program Files\Autodesk\AutoCAD 2025')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $repoRoot '.runtime/dotnet'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    dotnet restore CadMcp.sln --locked-mode --packages .runtime/packages -m:1 /nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
    dotnet build CadMcp.sln --no-restore -c Release -m:1 /nodeReuse:false "-p:AutoCADDir=$AutoCADDir"
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
} finally { Pop-Location }
