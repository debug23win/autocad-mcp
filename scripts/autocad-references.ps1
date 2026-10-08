# Collects the AutoCAD 2025 .NET reference assemblies (NuGet AutoCAD.NET 25.0.x) in one folder, so that
# CadMcp.AutoCAD and the probes compile where AutoCAD is not installed, for example in CI:
#   ./scripts/autocad-references.ps1 -Destination <folder>; ./scripts/build.ps1 -AutoCADDir <folder>
# The assemblies are used only for compilation and are never copied into build output or the installer.
param([Parameter(Mandatory=$true)][string]$Destination)
$ErrorActionPreference = 'Stop'
$packages = @(
    @{ Id = 'autocad.net'; Version = '25.0.1'; Sha256 = 'B629F09E10BB7F414460E1AD47E4EFA6D24D2815D00DEF388A64B10570CCD4C1' },
    @{ Id = 'autocad.net.core'; Version = '25.0.0'; Sha256 = '167A3B003D30230197CC150911080815BD5299E8E28FA411C64498E8E830EA53' },
    @{ Id = 'autocad.net.model'; Version = '25.0.0'; Sha256 = '06779A73F5DA2EED6A98C063EA13CDE4EB07056B088772FCA91C93ECDC770283' })
Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$work = Join-Path ([IO.Path]::GetTempPath()) ('cadmcp-autocad-references-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
try {
    foreach ($package in $packages) {
        $name = "$($package.Id).$($package.Version)"
        $file = Join-Path $work "$name.nupkg"
        Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/$($package.Id)/$($package.Version)/$name.nupkg" -OutFile $file
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $package.Sha256) { throw "Checksum mismatch: $name" }
        $folder = Join-Path $work $name
        [System.IO.Compression.ZipFile]::ExtractToDirectory($file, $folder)
        Get-ChildItem -LiteralPath (Join-Path $folder 'lib/net8.0') -Filter '*.dll' | Copy-Item -Destination $Destination -Force
    }
    foreach ($required in @('AcMgd.dll', 'AcCoreMgd.dll', 'AcDbMgd.dll', 'AcDbMgdBrep.dll', 'AcWindows.dll', 'AdWindows.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $Destination $required))) { throw "Missing reference assembly: $required" }
    }
    (Resolve-Path -LiteralPath $Destination).Path
} finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
