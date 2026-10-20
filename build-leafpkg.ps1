<# Build and package only. This script never installs, registers, reloads, or restarts Leaf. #>
[CmdletBinding()]
param(
    [string]$OutputDir = (Join-Path $PSScriptRoot 'dist'),
    [Parameter(Mandatory = $true)][string]$LeafSdkProject,
    [switch]$AllowDirty
)
$ErrorActionPreference = 'Stop'
if (-not $env:REDLEAF_SCRATCH_DIR) { throw 'REDLEAF_SCRATCH_DIR is required' }
if (-not (Test-Path -LiteralPath $LeafSdkProject)) { throw 'Leaf SDK project not found' }
$dirty = (& git -C $PSScriptRoot status --porcelain)
if ($dirty -and -not $AllowDirty) { throw 'Commit reviewed source first, or pass -AllowDirty for a local candidate build' }
$scratchRoot = [IO.Path]::GetFullPath($env:REDLEAF_SCRATCH_DIR)
$buildRoot = Join-Path $scratchRoot ('leaf-pixoo/build-' + [Guid]::NewGuid().ToString('N'))
$staging = Join-Path $buildRoot 'package'
$backend = Join-Path $staging 'backend'
New-Item -ItemType Directory -Force -Path $backend,$OutputDir | Out-Null

& dotnet build $LeafSdkProject -c Release --artifacts-path (Join-Path $buildRoot 'sdk') --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'SDK build failed' }
$sdkAssembly = Join-Path $buildRoot 'sdk/bin/Leaf.Sdk/release/Leaf.Sdk.dll'
& dotnet publish (Join-Path $PSScriptRoot 'src/Leaf.Plugins.Pixoo/Leaf.Plugins.Pixoo.csproj') -c Release -o $backend "-p:LeafSdkAssembly=$sdkAssembly" "-p:PixooArtifacts=$buildRoot/backend" --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Backend publish failed' }
foreach ($base in @('Leaf.Sdk','RedBamboo.AppHost')) {
    foreach ($extension in @('.dll','.pdb','.xml')) {
        $file = Join-Path $backend ($base + $extension)
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
    }
}

$oldOutput = $env:PIXOO_WEB_OUTPUT
try {
    $env:PIXOO_WEB_OUTPUT = Join-Path $staging 'web/dist'
    Push-Location (Join-Path $PSScriptRoot 'web')
    try {
        & pnpm install --frozen-lockfile --store-dir (Join-Path $scratchRoot 'leaf-pixoo/pnpm-store')
        if ($LASTEXITCODE -ne 0) { throw 'Frontend dependency install failed' }
        & pnpm run typecheck
        if ($LASTEXITCODE -ne 0) { throw 'Frontend typecheck failed' }
        & pnpm run build:pkg
        if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed' }
    } finally { Pop-Location }
} finally { $env:PIXOO_WEB_OUTPUT = $oldOutput }

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'plugin.json') -Destination $staging
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'seeds') -Destination $staging -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $staging
$files = Get-ChildItem -LiteralPath $staging -File -Recurse | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{ path = [IO.Path]::GetRelativePath($staging,$_.FullName).Replace('\','/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$files | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $staging 'file-manifest.json') -Encoding utf8
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'plugin.json') -Raw | ConvertFrom-Json
$packagePath = Join-Path $OutputDir ($manifest.id + '-' + $manifest.version + '.leafpkg')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$candidate = Join-Path $buildRoot 'candidate.leafpkg'
[IO.Compression.ZipFile]::CreateFromDirectory($staging,$candidate)
Copy-Item -LiteralPath $candidate -Destination $packagePath -Force
$hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$packagePath.sha256","$hash`n")
[pscustomobject]@{ package=$packagePath; sha256=$hash; files=$files.Count; deployed=$false; loaded=$false; buildRoot=$buildRoot } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDir 'package-receipt.json') -Encoding utf8
Write-Host "Package: $packagePath"
Write-Host "SHA256: $hash"
Write-Host 'Not installed or loaded.'
