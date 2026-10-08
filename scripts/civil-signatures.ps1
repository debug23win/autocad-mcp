# Regenerates tests/CadMcp.Tests/Fixtures/civil-api-signatures.txt from Autodesk's Civil3D.NET packages (the Civil 3D
# .NET API SDK on NuGet), one per supported release. Only the metadata of the assemblies is read; nothing from the
# packages is kept in the repository. Add a release by appending its package here and to CivilApiContract's test.
#   ./scripts/civil-signatures.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$packages = @(
    @{ Release = '2024'; Version = '13.6.1781'; Title = 'Civil 3D 2024.3 .NET API'; Sha256 = '68F341206301C85D2E11ACD87266F35894928206E03F0AFEBFBFA294D227D3DA' },
    @{ Release = '2025'; Version = '13.7.1175'; Title = 'Civil 3D 2025.2 .NET API'; Sha256 = '3EDAFC89A17F2FFC569E447ABB642E812869FBF92A86785357CD415837BF33A0' },
    @{ Release = '2026'; Version = '13.8.1516'; Title = 'Civil 3D 2026 .NET API'; Sha256 = '78F6E25A1F5D9248D981F88EC3FC5C6B01C603D4F80471EF747595DB0A1A821F' },
    @{ Release = '2027'; Version = '13.9.628'; Title = 'Civil 3D 2027 .NET API'; Sha256 = '89445F57314C11924556D8827B79D2A541EFAD28F38777D5D8655F0A6B197FC9' })
Add-Type -AssemblyName System.IO.Compression.FileSystem
$work = Join-Path ([IO.Path]::GetTempPath()) ('cadmcp-civil-signatures-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
try {
    $arguments = @('--out', (Join-Path $root 'tests/CadMcp.Tests/Fixtures/civil-api-signatures.txt'))
    foreach ($package in $packages) {
        $name = "civil3d.net.$($package.Version)"
        $file = Join-Path $work "$name.nupkg"
        Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/civil3d.net/$($package.Version)/$name.nupkg" -OutFile $file
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $package.Sha256) { throw "Checksum mismatch: $name" }
        $folder = Join-Path $work $name
        [System.IO.Compression.ZipFile]::ExtractToDirectory($file, $folder)
        $lib = Get-ChildItem -LiteralPath (Join-Path $folder 'lib') -Directory | Select-Object -First 1
        $arguments += @('--source', "Civil3D.NET $($package.Version) ($($package.Title))", "$($package.Release)=$($lib.FullName)")
    }
    dotnet run --project (Join-Path $root 'tests/CadMcp.CivilSignatures/CadMcp.CivilSignatures.csproj') -c Release -- @arguments
    if ($LASTEXITCODE -ne 0) { throw "Signature dump failed" }
} finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
