#Requires -Version 5.1
<#
.SYNOPSIS
    Build Release VSIX and publish to the self-hosted update server.

.DESCRIPTION
    1. Build MssqlIntelliSense.SsmsHost in Release mode (auto-bumps patch version)
    2. Copy the new .vsix to the update server's releases/ directory
    3. Update version.json with the new version number

.PARAMETER ServerDir
    Path to the update server working directory (where releases/ and version.json live).
    Defaults to: .\server-data

.PARAMETER Changelog
    Optional changelog text to include in version.json.

.EXAMPLE
    .\scripts\publish-update.ps1
    .\scripts\publish-update.ps1 -Changelog "Fixed completion bug; improved formatting"
    .\scripts\publish-update.ps1 -ServerDir "D:\update-server\data"
#>
param (
    [string]$ServerDir  = "",
    [string]$Changelog  = ""
)

$ErrorActionPreference = "Stop"
$ScriptDir   = Split-Path $MyInvocation.MyCommand.Path
$RepoRoot    = Resolve-Path (Join-Path $ScriptDir "..")
$ProjectDir  = Join-Path $RepoRoot "src\MssqlIntelliSense.SsmsHost"
$ProjectFile = Join-Path $ProjectDir "MssqlIntelliSense.SsmsHost.csproj"
$VersionJson = Join-Path $RepoRoot "version.json"

if ([string]::IsNullOrEmpty($ServerDir)) {
    $ServerDir = Join-Path $RepoRoot "server-data"
}
$ReleasesDir = Join-Path $ServerDir "releases"

Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  MssqlIntelliSense Update Publisher" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""

# ── 1. BUILD RELEASE ──────────────────────────────────────────────────────────
Write-Host "[1/4] Building Release..." -ForegroundColor Yellow
& dotnet build $ProjectFile --configuration Release --verbosity minimal
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed. Aborting."
    exit 1
}
Write-Host "      Build OK" -ForegroundColor Green

# ── 2. READ NEW VERSION FROM MANIFEST ────────────────────────────────────────
Write-Host ""
Write-Host "[2/4] Reading version from manifest..." -ForegroundColor Yellow
$manifestPath = Join-Path $ProjectDir "source.extension.vsixmanifest"
$manifestContent = Get-Content $manifestPath -Raw
if ($manifestContent -match 'Identity\s[^>]*Version="(\d+\.\d+\.\d+)"') {
    $newVersion = $Matches[1]
} else {
    Write-Error "Could not parse version from vsixmanifest."
    exit 1
}
Write-Host "      Version: $newVersion" -ForegroundColor Green

# ── 3. COPY VSIX TO RELEASES DIR ─────────────────────────────────────────────
Write-Host ""
Write-Host "[3/4] Publishing VSIX to server releases dir..." -ForegroundColor Yellow
$vsixSrc = Join-Path $ProjectDir "bin\Release\net472\MssqlIntelliSense.SsmsHost.vsix"
if (-not (Test-Path $vsixSrc)) {
    Write-Error "VSIX not found at: $vsixSrc"
    exit 1
}

New-Item -ItemType Directory -Path $ReleasesDir -Force | Out-Null
$vsixDest = Join-Path $ReleasesDir "MssqlIntelliSense.SsmsHost-$newVersion.vsix"
Copy-Item $vsixSrc $vsixDest -Force
Write-Host "      Copied to: $vsixDest" -ForegroundColor Green

# Keep only the last 5 releases to save disk space
$oldFiles = Get-ChildItem $ReleasesDir -Filter "*.vsix" |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -Skip 5
foreach ($f in $oldFiles) {
    Remove-Item $f.FullName -Force
    Write-Host "      Removed old release: $($f.Name)" -ForegroundColor Gray
}

# ── 4. UPDATE version.json ────────────────────────────────────────────────────
Write-Host ""
Write-Host "[4/4] Updating version.json..." -ForegroundColor Yellow

# Read existing version.json
$versionData = Get-Content $VersionJson -Raw | ConvertFrom-Json

# Update fields
$versionData.version = $newVersion
if (-not [string]::IsNullOrEmpty($Changelog)) {
    $versionData.changelog = $Changelog
}

# Write back (preserve url field, don't hardcode vsixUrl – server injects it at runtime)
$versionData | ConvertTo-Json -Depth 5 | Set-Content $VersionJson -Encoding UTF8

# Also copy version.json into server-data so server reads the latest
$serverVersionJson = Join-Path $ServerDir "version.json"
Copy-Item $VersionJson $serverVersionJson -Force

Write-Host "      version.json updated: $newVersion" -ForegroundColor Green

Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  Published v$newVersion successfully!" -ForegroundColor Green
Write-Host ""
Write-Host "  Server data dir : $ServerDir" -ForegroundColor White
Write-Host "  Releases dir    : $ReleasesDir" -ForegroundColor White
Write-Host ""
Write-Host "  Start server with:" -ForegroundColor White
Write-Host "    dotnet run --project src\MssqlIntelliSense.UpdateServer -- --urls http://0.0.0.0:5100" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""
