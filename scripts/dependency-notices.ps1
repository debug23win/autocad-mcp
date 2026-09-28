$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$lock = Get-Content -LiteralPath (Join-Path $repoRoot 'src/CadMcp.Host/packages.lock.json') -Raw | ConvertFrom-Json -AsHashtable
$entries = @()
$destination = Join-Path $repoRoot 'licenses/dependencies'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
foreach ($framework in $lock.dependencies.Values) {
    foreach ($id in ($framework.Keys | Sort-Object)) {
        $package = $framework[$id]
        if ($package.type -eq 'Project') { continue }
        $version = $package.resolved
        $folder = Join-Path $repoRoot ".runtime/packages/$($id.ToLowerInvariant())/$version"
        [xml]$nuspec = Get-Content -LiteralPath (Join-Path $folder "$($id.ToLowerInvariant()).nuspec")
        $license = $nuspec.package.metadata.license.InnerText
        $copied = @()
        foreach ($item in (Get-ChildItem -LiteralPath $folder -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES|NOTICE)' })) {
            $target = "$id-$version-$($item.Name)"
            Copy-Item -LiteralPath $item.FullName -Destination (Join-Path $destination $target)
            $copied += "licenses/dependencies/$target"
        }
        $entries += [pscustomobject]@{ id=$id; version=$version; declared_license=$license; content_hash=$package.contentHash; notice_files=$copied }
    }
}
$entries | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $repoRoot 'licenses/dependencies.json') -Encoding utf8
