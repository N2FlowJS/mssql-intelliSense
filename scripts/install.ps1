#Requires -Version 5.1
<#
.SYNOPSIS
    1-Click Automated Installer for MSSQL IntelliSense SSMS Extension.

.DESCRIPTION
    Detects all SSMS installations (SSMS 18, 19, 20, 21, 22), closes running SSMS instances,
    deploys extension binaries to the Extensions folder, and clears ComponentModelCache.

.PARAMETER SourceDir
    Path to the source extension binaries. Defaults to build output or script directory.

.PARAMETER Launch
    Automatically launch SSMS after installation completes.

.PARAMETER NoKill
    Skip killing existing SSMS processes before deploying.

.EXAMPLE
    .\scripts\install.ps1
    .\scripts\install.ps1 -Launch
#>
param (
    [string]$SourceDir = "",
    [switch]$Launch,
    [switch]$NoKill
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path $MyInvocation.MyCommand.Path
$RepoRoot  = Resolve-Path (Join-Path $ScriptDir "..")

. (Join-Path $ScriptDir "shared.ps1")

if ([string]::IsNullOrEmpty($SourceDir)) {
    $possiblePaths = @(
        (Join-Path $ScriptDir "bin"),
        (Join-Path $RepoRoot "src\MssqlIntelliSense.SsmsHost\bin\Release\net472"),
        (Join-Path $RepoRoot "src\MssqlIntelliSense.SsmsHost\bin\Debug\net472")
    )
    foreach ($p in $possiblePaths) {
        if (Test-Path (Join-Path $p "MssqlIntelliSense.SsmsHost.dll")) {
            $SourceDir = $p
            break
        }
    }
}

if ([string]::IsNullOrEmpty($SourceDir) -or -not (Test-Path (Join-Path $SourceDir "MssqlIntelliSense.SsmsHost.dll"))) {
    Write-Error "Could not locate extension binaries. Please run 'dotnet build' first or pass -SourceDir."
    exit 1
}

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  MSSQL IntelliSense - Automated Installer" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "Source directory: $SourceDir" -ForegroundColor Gray
Write-Host ""

# 1. Close SSMS processes
Write-Host "[1/4] Closing running SSMS processes..." -ForegroundColor Yellow
$stopped = Stop-SsmsProcesses -Skip:$NoKill
if ($stopped) {
    Write-Host "      SSMS closed." -ForegroundColor Green
} else {
    Write-Host "      No running SSMS process found." -ForegroundColor Gray
}

# 2. Locate SSMS AppData Extension directories
Write-Host ""
Write-Host "[2/4] Locating SSMS extension directories..." -ForegroundColor Yellow
$ssmsDirs = Get-SsmsDirectories
if ($ssmsDirs.Count -eq 0) {
    Write-Error "SSMS AppData directory not found at $(Join-Path $env:LOCALAPPDATA 'Microsoft\SSMS')."
    exit 1
}

# 3. Deploy binaries to all detected SSMS extension locations
Write-Host ""
Write-Host "[3/4] Deploying extension binaries..." -ForegroundColor Yellow
$installedCount = 0

foreach ($ssmsDir in $ssmsDirs) {
    $extRoot = Join-Path $ssmsDir.FullName "Extensions"
    $targetDir = Join-Path $extRoot "MssqlIntelliSense.SsmsHost"

    try {
        if (-not (Test-Path $targetDir)) {
            New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
        }

        Remove-LegacySqliteFiles -Path $targetDir
        Invoke-Robocopy -Source $SourceDir -Destination $targetDir

        $configChangedFile = Join-Path $extRoot "extensions.configurationchanged"
        New-Item -ItemType File -Path $configChangedFile -Force | Out-Null

        Clear-ComponentModelCache -SsmsDir $ssmsDir.FullName | Out-Null

        Write-Host "      Installed to: $($ssmsDir.Name)" -ForegroundColor Green
        $installedCount++
    }
    catch {
        Write-Warning "      Failed to deploy to $($ssmsDir.Name): $_"
    }
}

if ($installedCount -eq 0) {
    Write-Error "Deployment failed for all SSMS directories."
    exit 1
}

Write-Host ""
Write-Host "[4/4] Installation completed successfully!" -ForegroundColor Green

# 4. Optional Launch SSMS
if ($Launch) {
    Write-Host ""
    Write-Host "Launching SSMS..." -ForegroundColor Cyan
    $ssmsExe = Get-SsmsExecutable
    if ($ssmsExe) {
        Start-Process -FilePath $ssmsExe
    }
}
