param(
    [Parameter(Mandatory=$true)][string]$IsccPath,
    [string]$AutoCADDir = 'C:\Program Files\Autodesk\AutoCAD 2025',
    [Parameter(Mandatory=$true)][string]$CodexDir,
    [Parameter(Mandatory=$true)][string]$DotNet2027
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $repoRoot '.runtime/dotnet'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $payload = Join-Path $repoRoot "artifacts/installer-$stamp/payload"
    $codexAssets = Get-Content -LiteralPath (Join-Path $repoRoot 'installer/codex-assets.json') -Raw | ConvertFrom-Json
    $codexExe = Join-Path $CodexDir 'codex-x86_64-pc-windows-msvc.exe'
    foreach ($required in @($codexExe, (Join-Path $CodexDir 'LICENSE'), (Join-Path $CodexDir 'NOTICE'), (Join-Path $CodexDir 'release.json'), (Join-Path $CodexDir 'ratatui-MIT.txt'), (Join-Path $CodexDir 'ripgrep-MIT.txt'))) {
        if (!(Test-Path -LiteralPath $required)) { throw "Missing official Codex file: $required" }
    }
    if ((Get-FileHash -LiteralPath $codexExe -Algorithm SHA256).Hash -ne $codexAssets.executable_sha256) { throw 'Codex executable checksum mismatch' }
    foreach ($file in $codexAssets.files) {
        $filePath = Join-Path (Join-Path $CodexDir 'package') $file.path
        if ((Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash -ne $file.sha256) { throw "Codex package checksum mismatch: $($file.path)" }
    }
    $cliVersion = & $codexExe --version
    if ($LASTEXITCODE -ne 0 -or $cliVersion -ne 'codex-cli 0.159.0') { throw 'Expected verified official Codex 0.159.0' }
    New-Item -ItemType Directory -Force -Path "$payload/Contents/Win64","$payload/Contents/Host","$payload/Contents/Net10","$payload/Contents/Client" | Out-Null
    dotnet build CadMcp.sln -c Release -m:1 /nodeReuse:false "-p:AutoCADDir=$AutoCADDir" --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    dotnet publish src/CadMcp.Host/CadMcp.Host.csproj -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=8.0.31 -p:PublishSingleFile=false -p:PublishTrimmed=false --no-restore -o "$payload/Contents/Host" -m:1 /nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    dotnet publish src/CadMcp.Client/CadMcp.Client.csproj -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=8.0.31 -p:PublishSingleFile=false -p:PublishTrimmed=false --no-restore -o "$payload/Contents/Client" -m:1 /nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw 'Client publish failed' }
    & $DotNet2027 build src/CadMcp.AutoCAD2027/CadMcp.AutoCAD2027.csproj -c Release --no-restore -m:1 /nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw 'Official SDK 2027 build failed' }
    $native2027 = Join-Path $repoRoot 'src/CadMcp.AutoCAD2027/bin/Release/net10.0-windows'
    foreach ($name in @('CadMcp.AutoCAD2027.dll','CadMcp.Core.dll','CadMcp.AutoCAD2027.deps.json')) {
        Copy-Item -LiteralPath (Join-Path $native2027 $name) -Destination "$payload/Contents/Net10"
    }
    $native = Join-Path $repoRoot 'src/CadMcp.AutoCAD/bin/Release/net8.0-windows'
    foreach ($name in @('CadMcp.AutoCAD.dll','CadMcp.Core.dll','CadMcp.Providers.dll','CadMcp.AutoCAD.deps.json')) {
        Copy-Item -LiteralPath (Join-Path $native $name) -Destination "$payload/Contents/Win64"
    }
    Copy-Item -LiteralPath installer/PackageContents.xml -Destination $payload
    Copy-Item -LiteralPath installer/INSTALL.txt -Destination $payload
    $codexPayload = Join-Path $payload 'Contents/Tools/Codex'
    New-Item -ItemType Directory -Force -Path $codexPayload | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $CodexDir 'package') | Copy-Item -Destination $codexPayload -Recurse
    Copy-Item -LiteralPath $codexExe -Destination (Join-Path $codexPayload 'bin/codex.exe')
    foreach ($name in @('LICENSE','NOTICE','release.json','ratatui-MIT.txt','ripgrep-MIT.txt')) {
        Copy-Item -LiteralPath (Join-Path $CodexDir $name) -Destination $codexPayload
    }
    Copy-Item -LiteralPath 'installer/codex-assets.json' -Destination (Join-Path $codexPayload 'pinned-assets.json')
    foreach ($name in @('README.md','LICENSE','NOTICE','licenses','docs')) {
        Copy-Item -LiteralPath $name -Destination $payload -Recurse
    }
    $runtimeNotices = Join-Path $payload 'licenses/dotnet-runtime-8.0.31'
    New-Item -ItemType Directory -Force -Path $runtimeNotices | Out-Null
    Get-ChildItem -LiteralPath '.runtime/packages/microsoft.netcore.app.runtime.win-x64/8.0.31' -File |
        Where-Object { $_.Name -match 'LICENSE|NOTICE' } |
        Copy-Item -Destination $runtimeNotices
    $desktopNotices = Join-Path $payload 'licenses/dotnet-desktop-runtime-8.0.31'
    New-Item -ItemType Directory -Force -Path $desktopNotices | Out-Null
    Get-ChildItem -LiteralPath '.runtime/packages/microsoft.windowsdesktop.app.runtime.win-x64/8.0.31' -File |
        Where-Object { $_.Name -match 'LICENSE|NOTICE' } | Copy-Item -Destination $desktopNotices
    if (Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object { $_.Name -match '^(Ac(Mgd|CoreMgd|DbMgd|Windows)|AdWindows)\.dll$' }) { throw 'Autodesk SDK assemblies must not be redistributed' }
    Get-ChildItem -LiteralPath $payload -File -Recurse | ForEach-Object {
        [pscustomobject]@{path=[System.IO.Path]::GetRelativePath($payload,$_.FullName).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    } | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath "$payload/payload-manifest.json" -Encoding utf8
    $output = Split-Path -Parent $repoRoot
    & $IsccPath /Qp "/DPayloadDir=$payload" "/DOutputDir=$output" installer/setup.iss
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
    Get-FileHash -LiteralPath (Join-Path $output 'CAD-MCP-2025-2027-0.3.2-preview-Setup.exe') -Algorithm SHA256
} finally { Pop-Location }
