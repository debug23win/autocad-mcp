$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$packageRoot = Join-Path $repoRoot "artifacts/stage3a-$stamp"
New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
foreach ($name in @('README.md','LICENSE','NOTICE','Directory.Build.props','CadMcp.sln','.gitignore','.gitattributes')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $name) -Destination $packageRoot
}
foreach ($name in @('docs','licenses','scripts')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $name) -Destination $packageRoot -Recurse
}
foreach ($name in @('src','tests')) {
    foreach ($file in (Get-ChildItem -LiteralPath (Join-Path $repoRoot $name) -File -Recurse | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })) {
        $relative = [System.IO.Path]::GetRelativePath($repoRoot, $file.FullName)
        $target = Join-Path $packageRoot $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}
$nativeOutput = Join-Path $repoRoot 'src/CadMcp.AutoCAD/bin/Release/net8.0-windows'
if (Get-ChildItem -LiteralPath $nativeOutput -Filter 'Ac*.dll') { throw 'Autodesk DLL unexpectedly included in output' }
New-Item -ItemType Directory -Force -Path (Join-Path $packageRoot 'build') | Out-Null
Copy-Item -LiteralPath $nativeOutput -Destination (Join-Path $packageRoot 'build/AutoCAD2025') -Recurse
Copy-Item -LiteralPath (Join-Path $repoRoot 'src/CadMcp.Host/bin/Release/net8.0-windows') -Destination (Join-Path $packageRoot 'build/Host') -Recurse
$archive = Join-Path (Split-Path -Parent $repoRoot) "autocad-mcp-stage3a-$stamp.zip"
Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $archive
Get-FileHash -LiteralPath $archive -Algorithm SHA256
