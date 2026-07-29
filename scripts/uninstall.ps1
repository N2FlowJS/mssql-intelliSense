#Requires -Version 5.1
<#
.SYNOPSIS
    1-Click Automated Uninstaller for MSSQL IntelliSense SSMS Extension.

.DESCRIPTION
    Closes running SSMS processes, removes MssqlIntelliSense.SsmsHost extension directories,
    and clears ComponentModelCache across all SSMS versions.
#>
param (
    [switch]$NoKill
)

$ErrorActionPreference = "Stop"

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  MSSQL IntelliSense - Automated Uninstaller" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""

# 1. Close SSMS processes
if (-not $NoKill) {
    Write-Host "[1/3] Closing running SSMS processes..." -ForegroundColor Yellow
    $ssmsProcesses = Get-Process -Name "Ssms" -ErrorAction SilentlyContinue
    if ($ssmsProcesses) {
        $ssmsProcesses | Stop-Process -Force
        Start-Sleep -Seconds 2
        Write-Host "      SSMS closed." -ForegroundColor Green
    }
}

# 2. Locate SSMS AppData Extension directories
Write-Host ""
Write-Host "[2/3] Locating SSMS extension directories..." -ForegroundColor Yellow
$ssmsRoot = Join-Path $env:LOCALAPPDATA "Microsoft\SSMS"
if (-not (Test-Path $ssmsRoot)) {
    Write-Host "SSMS AppData directory not found. Nothing to uninstall." -ForegroundColor Gray
    exit 0
}

$ssmsDirs = Get-ChildItem $ssmsRoot -Directory

# 3. Remove extension files
Write-Host ""
Write-Host "[3/3] Removing extension files..." -ForegroundColor Yellow
$removedCount = 0

foreach ($ssmsDir in $ssmsDirs) {
    $extRoot = Join-Path $ssmsDir.FullName "Extensions"
    if (Test-Path $extRoot) {
        $targetDirs = Get-ChildItem -Path $extRoot -Directory | Where-Object {
            $_.Name -eq "MssqlIntelliSense.SsmsHost" -or
            (Test-Path (Join-Path $_.FullName "MssqlIntelliSense.SsmsHost.dll"))
        }

        foreach ($targetDir in $targetDirs) {
            try {
                Remove-Item -Path $targetDir.FullName -Recurse -Force -ErrorAction SilentlyContinue
                Write-Host "      Removed from: $($ssmsDir.Name)" -ForegroundColor Green
                $removedCount++
            }
            catch {
                Write-Warning "      Failed to remove from $($ssmsDir.Name): $_"
            }
        }

        # Clear ComponentModelCache
        $cacheDir = Join-Path $ssmsDir.FullName "ComponentModelCache"
        if (Test-Path $cacheDir) {
            Remove-Item -Path $cacheDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

Write-Host ""
Write-Host "Uninstallation completed successfully! ($removedCount directory removed)" -ForegroundColor Green
