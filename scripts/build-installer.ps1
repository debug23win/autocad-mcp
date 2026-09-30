param(
    [Parameter(Mandatory=$true)][string]$IsccPath,
    [string]$AutoCADDir = 'C:\Program Files\Autodesk\AutoCAD 2025',
    [string]$CodexDir,
    [string]$CodexPayloadDir,
    [string]$StageRoot,
    [string]$InstallerOutput,
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
    $stage = if ($StageRoot) { $StageRoot } else { Join-Path $repoRoot 'artifacts' }
    $payload = Join-Path $stage "installer-$stamp/payload"
    $codexAssets = Get-Content -LiteralPath (Join-Path $repoRoot 'installer/codex-assets.json') -Raw | ConvertFrom-Json
    if ([bool]$CodexDir -eq [bool]$CodexPayloadDir) { throw 'Supply exactly one of CodexDir or CodexPayloadDir' }
    $codexSource = if ($CodexPayloadDir) { $CodexPayloadDir } else { $CodexDir }
    $codexPackageRoot = if ($CodexPayloadDir) { $CodexPayloadDir } else { Join-Path $CodexDir 'package' }
    $codexExe = if ($CodexPayloadDir) { Join-Path $CodexPayloadDir 'bin/codex.exe' } else { Join-Path $CodexDir 'codex-x86_64-pc-windows-msvc.exe' }
    foreach ($required in @($codexExe, (Join-Path $codexSource 'LICENSE'), (Join-Path $codexSource 'NOTICE'), (Join-Path $codexSource 'release.json'), (Join-Path $codexSource 'ratatui-MIT.txt'), (Join-Path $codexSource 'ripgrep-MIT.txt'))) {
        if (!(Test-Path -LiteralPath $required)) { throw "Missing official Codex file: $required" }
    }
    if ((Get-FileHash -LiteralPath $codexExe -Algorithm SHA256).Hash -ne $codexAssets.executable_sha256) { throw 'Codex executable checksum mismatch' }
    foreach ($file in $codexAssets.files) {
        $filePath = Join-Path $codexPackageRoot $file.path
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
    & $DotNet2027 build src/CadMcp.AutoCAD2027/CadMcp.AutoCAD2027.csproj -c Release --no-restore -p:BuildProjectReferences=false -m:1 /nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw 'Official SDK 2027 build failed' }
    $native2027 = Join-Path $repoRoot 'src/CadMcp.AutoCAD2027/bin/Release/net10.0-windows'
    foreach ($name in @('CadMcp.AutoCAD2027.dll','CadMcp.Core.dll','CadMcp.AutoCAD2027.deps.json')) {
        Copy-Item -LiteralPath (Join-Path $native2027 $name) -Destination "$payload/Contents/Net10"
    }
    $native = Join-Path $repoRoot 'src/CadMcp.AutoCAD/bin/Release/net8.0-windows'
    Get-ChildItem -LiteralPath $native -File -Filter '*.dll' |
        Where-Object { $_.Name -notmatch '^(AcMgd|AcCoreMgd|AcDbMgd|AcWindows|AdWindows)\.dll$' } |
        Copy-Item -Destination "$payload/Contents/Win64"
    Copy-Item -LiteralPath (Join-Path $native 'CadMcp.AutoCAD.deps.json') -Destination "$payload/Contents/Win64"
    Copy-Item -LiteralPath (Join-Path $native 'Resources') -Destination "$payload/Contents/Win64" -Recurse
    Copy-Item -LiteralPath (Join-Path $native 'runtimes') -Destination "$payload/Contents/Win64" -Recurse
    Copy-Item -LiteralPath installer/PackageContents.xml -Destination $payload
    Copy-Item -LiteralPath installer/INSTALL.txt -Destination $payload
    $codexPayload = Join-Path $payload 'Contents/Tools/Codex'
    New-Item -ItemType Directory -Force -Path $codexPayload | Out-Null
    if ($CodexPayloadDir) {
        $sourceRoot = (Resolve-Path -LiteralPath $CodexPayloadDir).Path
        Get-ChildItem -LiteralPath $sourceRoot -File -Recurse | ForEach-Object {
            $relative = [IO.Path]::GetRelativePath($sourceRoot, $_.FullName)
            $target = Join-Path $codexPayload $relative
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
            if ([IO.Path]::GetPathRoot($sourceRoot) -eq [IO.Path]::GetPathRoot($codexPayload)) {
                try { New-Item -ItemType HardLink -Path $target -Target $_.FullName | Out-Null }
                catch { Copy-Item -LiteralPath $_.FullName -Destination $target }
            } else { Copy-Item -LiteralPath $_.FullName -Destination $target }
        }
    } else {
        Get-ChildItem -LiteralPath (Join-Path $CodexDir 'package') | Copy-Item -Destination $codexPayload -Recurse
        Copy-Item -LiteralPath $codexExe -Destination (Join-Path $codexPayload 'bin/codex.exe')
        foreach ($name in @('LICENSE','NOTICE','release.json','ratatui-MIT.txt','ripgrep-MIT.txt')) {
            Copy-Item -LiteralPath (Join-Path $CodexDir $name) -Destination $codexPayload
        }
        Copy-Item -LiteralPath 'installer/codex-assets.json' -Destination (Join-Path $codexPayload 'pinned-assets.json')
    }
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
    $output = if ($InstallerOutput) { $InstallerOutput } else { Split-Path -Parent $repoRoot }
    New-Item -ItemType Directory -Force -Path $output | Out-Null
    & $IsccPath /Qp "/DPayloadDir=$payload" "/DOutputDir=$output" installer/setup.iss
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
    Get-FileHash -LiteralPath (Join-Path $output 'CAD-MCP-2025-2027-0.6.0-preview-Setup.exe') -Algorithm SHA256
} finally { Pop-Location }
