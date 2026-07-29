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
if (-not $NoKill) {
    Write-Host "[1/4] Closing running SSMS processes..." -ForegroundColor Yellow
    $ssmsProcesses = Get-Process -Name "Ssms" -ErrorAction SilentlyContinue
    if ($ssmsProcesses) {
        $ssmsProcesses | Stop-Process -Force
        Start-Sleep -Seconds 2
        Write-Host "      SSMS closed." -ForegroundColor Green
    } else {
        Write-Host "      No running SSMS process found." -ForegroundColor Gray
    }
} else {
    Write-Host "[1/4] Skipping SSMS termination (-NoKill)." -ForegroundColor Gray
}

# 2. Locate SSMS AppData Extension directories
Write-Host ""
Write-Host "[2/4] Locating SSMS extension directories..." -ForegroundColor Yellow
$ssmsRoot = Join-Path $env:LOCALAPPDATA "Microsoft\SSMS"
if (-not (Test-Path $ssmsRoot)) {
    Write-Error "SSMS AppData directory not found at $ssmsRoot."
    exit 1
}

$ssmsDirs = Get-ChildItem $ssmsRoot -Directory | Where-Object { $_.Name -match "^22\." -or $_.Name -match "^20\." -or $_.Name -match "^19\." -or $_.Name -match "^18\." }
if ($ssmsDirs.Count -eq 0) {
    $ssmsDirs = Get-ChildItem $ssmsRoot -Directory
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

        # Clean legacy SQLite DLLs if present
        Get-ChildItem -Path $targetDir -Recurse -File -ErrorAction SilentlyContinue | Where-Object {
            $_.Name -match 'SQLite|Sqlite|e_sqlite'
        } | Remove-Item -Force -ErrorAction SilentlyContinue

        # Copy binaries
        & robocopy $SourceDir $targetDir /E /IS /IT /XF *.vsix /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
        
        # Touch extensions.configurationchanged
        $configChangedFile = Join-Path $extRoot "extensions.configurationchanged"
        New-Item -ItemType File -Path $configChangedFile -Force | Out-Null

        # Clear ComponentModelCache to force registration
        $cacheDir = Join-Path $ssmsDir.FullName "ComponentModelCache"
        if (Test-Path $cacheDir) {
            Remove-Item -Path $cacheDir -Recurse -Force -ErrorAction SilentlyContinue
        }

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
    $ssmsExePaths = @(
        "C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Ssms.exe",
        "C:\Program Files\Microsoft SQL Server Management Studio 22\Common7\IDE\Ssms.exe",
        "C:\Program Files (x86)\Microsoft SQL Server Management Studio 20\Common7\IDE\Ssms.exe",
        "C:\Program Files (x86)\Microsoft SQL Server Management Studio 19\Common7\IDE\Ssms.exe",
        "C:\Program Files (x86)\Microsoft SQL Server Management Studio 18\Common7\IDE\Ssms.exe"
    )
    foreach ($exe in $ssmsExePaths) {
        if (Test-Path $exe) {
            Start-Process -FilePath $exe
            break
        }
    }
}
