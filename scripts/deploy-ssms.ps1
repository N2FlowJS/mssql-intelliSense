#Requires -Version 5.1
<#
.SYNOPSIS
    Deploy MSSQL IntelliSense extension binaries to SSMS.

.DESCRIPTION
    Copies extension binaries to all detected SSMS extension directories,
    removes legacy files, and clears ComponentModelCache.

.PARAMETER ProjectDir
    Path to the project directory. Defaults to src\MssqlIntelliSense.SsmsHost.

.PARAMETER TargetDir
    Path to build output binaries. Defaults to bin\Debug\net472 under ProjectDir.

.PARAMETER NoKill
    Skip killing existing SSMS processes before deploying.

.PARAMETER Launch
    Launch SSMS after deployment.

.EXAMPLE
    .\scripts\deploy-ssms.ps1
    .\scripts\deploy-ssms.ps1 -Launch
    .\scripts\deploy-ssms.ps1 -NoKill
#>
param (
    [string]$ProjectDir,
    [string]$TargetDir,
    [switch]$NoKill,
    [switch]$Launch
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path $MyInvocation.MyCommand.Path

. (Join-Path $ScriptDir "shared.ps1")

# Set defaults if not specified
if ([string]::IsNullOrEmpty($ProjectDir)) {
    $ProjectDir = Resolve-Path (Join-Path $ScriptDir "..\src\MssqlIntelliSense.SsmsHost")
}
if ([string]::IsNullOrEmpty($TargetDir)) {
    $TargetDir = Resolve-Path (Join-Path $ProjectDir "bin\Debug\net472")
}

Write-Host "Deploying SSMS Extension..." -ForegroundColor Cyan
Write-Host "Project directory: $ProjectDir"
Write-Host "Target directory:  $TargetDir"

# Kill SSMS processes by default to release assembly DLL locks
Write-Host ""
Write-Host "[1/3] Closing running SSMS processes..." -ForegroundColor Yellow
$stopped = Stop-SsmsProcesses -Skip:$NoKill
if ($stopped) {
    Write-Host "      SSMS closed." -ForegroundColor Green
} else {
    Write-Host "      No SSMS processes running." -ForegroundColor Gray
}

# Locate SSMS AppData directories
Write-Host ""
Write-Host "[2/3] Locating SSMS extension directories..." -ForegroundColor Yellow
$ssmsDirs = Get-SsmsDirectories

if ($ssmsDirs.Count -eq 0) {
    Write-Error "Could not locate SSMS AppData directory at $(Join-Path $env:LOCALAPPDATA 'Microsoft\SSMS')."
    exit 1
}

# Deploy unpacked extension files to SSMS Extensions folder
Write-Host ""
Write-Host "[3/3] Deploying extension binaries..." -ForegroundColor Yellow
$deployed = $false

foreach ($ssmsDir in $ssmsDirs) {
    $extRoot = Join-Path $ssmsDir.FullName "Extensions"
    $destDir = Join-Path $extRoot "MssqlIntelliSense.SsmsHost"
    $targetDirs = @($destDir)

    # Clean up legacy VSIXInstaller folders if present to avoid conflicts
    if (Test-Path $extRoot) {
        $legacyFolders = Get-ChildItem -Path $extRoot -Directory | Where-Object {
            $_.Name -ne "MssqlIntelliSense.SsmsHost" -and
            ((Test-Path (Join-Path $_.FullName "SqlPrompt.SsmsHost.dll")) -or
             (Test-Path (Join-Path $_.FullName "SqlPrompt.SsmsHost.pkgdef")))
        }
        foreach ($legacy in $legacyFolders) {
            Write-Host "      Removing legacy folder: $($legacy.FullName)" -ForegroundColor Yellow
            Remove-Item -Path $legacy.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }

        $installedFolders = Get-ChildItem -Path $extRoot -Directory | Where-Object {
            (Test-Path (Join-Path $_.FullName "MssqlIntelliSense.SsmsHost.dll")) -or
            (Test-Path (Join-Path $_.FullName "MssqlIntelliSense.SsmsHost.pkgdef"))
        } | Select-Object -ExpandProperty FullName

        $targetDirs = @($targetDirs + $installedFolders) | Select-Object -Unique
    }

    foreach ($targetDir in $targetDirs) {
        Write-Host "      Deploying to: $targetDir" -ForegroundColor Yellow
        try {
            if (-not (Test-Path $targetDir)) {
                New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
            }

            Remove-LegacySqliteFiles -Path $targetDir
            Invoke-Robocopy -Source $TargetDir -Destination $targetDir

            Write-Host "      Successfully deployed extension binaries." -ForegroundColor Green
            $deployed = $true
        }
        catch {
            Write-Warning "      Failed to deploy to $targetDir`: $_"
        }
    }

    # Touch extensions.configurationchanged to notify SSMS extension engine
    $configChangedFile = Join-Path $extRoot "extensions.configurationchanged"
    New-Item -ItemType File -Path $configChangedFile -Force | Out-Null

    # Clear ComponentModelCache to force SSMS to rebuild MEF & Package cache clean
    $cleared = Clear-ComponentModelCache -SsmsDir $ssmsDir.FullName
    if ($cleared) {
        Write-Host "      Cleared ComponentModelCache." -ForegroundColor Gray
    }
}

if (-not $deployed) {
    Write-Error "Deployment failed for all SSMS instances."
    exit 1
}

Write-Host ""
Write-Host "SSMS Extension deployment completed successfully!" -ForegroundColor Green

# Launch SSMS if requested
if ($Launch) {
    Write-Host ""
    $ssmsExe = Get-SsmsExecutable
    if ($ssmsExe) {
        Write-Host "Launching SSMS: $ssmsExe" -ForegroundColor Green
        Start-Process $ssmsExe
    } else {
        Write-Warning "Could not locate Ssms.exe automatically. Please launch SSMS manually."
    }
}
