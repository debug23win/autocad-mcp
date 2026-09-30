param([string]$Destination)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$metadataPath = Join-Path $repoRoot 'installer/codex-assets.json'
$metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
if (!$Destination) { $Destination = Join-Path $repoRoot "artifacts/codex-$($metadata.version)" }
$Destination = [IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
foreach ($asset in $metadata.archives) {
    $archive = Join-Path $Destination $asset.name
    if (!(Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri $asset.url -OutFile $archive }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $asset.sha256) { throw "Codex archive checksum mismatch: $($asset.name)" }
}
$package = Join-Path $Destination 'package'
New-Item -ItemType Directory -Force -Path $package | Out-Null
& tar -xf (Join-Path $Destination 'codex-app-server-package-x86_64-pc-windows-msvc.tar.gz') -C $package
if ($LASTEXITCODE -ne 0) { throw 'Codex app-server package extraction failed' }
& tar -xf (Join-Path $Destination 'codex-x86_64-pc-windows-msvc.exe.tar.gz') -C $Destination
if ($LASTEXITCODE -ne 0) { throw 'Codex CLI extraction failed' }
$cli = Join-Path $Destination 'codex-x86_64-pc-windows-msvc.exe'
if ((Get-FileHash -LiteralPath $cli -Algorithm SHA256).Hash -ne $metadata.executable_sha256) { throw 'Codex executable checksum mismatch' }
Copy-Item -LiteralPath $cli -Destination (Join-Path $package 'bin/codex.exe')
foreach ($file in $metadata.files) {
    $path = Join-Path $package $file.path
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { throw "Codex package checksum mismatch: $($file.path)" }
}
Get-ChildItem -LiteralPath (Join-Path $repoRoot 'installer/codex-licenses') -File | Copy-Item -Destination $Destination
Copy-Item -LiteralPath $metadataPath -Destination (Join-Path $Destination 'release.json')
Write-Output "Verified official Codex $($metadata.version): $Destination"
