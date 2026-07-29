#Requires -Version 5.1
<#
.SYNOPSIS
    Automated Release Installer Builder for MSSQL IntelliSense.

.DESCRIPTION
    1. Builds Release configuration for solution
    2. Packages binaries into dist/MssqlIntelliSense-v<Version>-Portable.zip (with install.ps1 & uninstall.ps1)
    3. Builds Inno Setup installer executable (dist/MssqlIntelliSense-Setup-v<Version>.exe) if ISCC.exe is available.
#>
param (
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$ScriptDir  = Split-Path $MyInvocation.MyCommand.Path
$RepoRoot   = Resolve-Path (Join-Path $ScriptDir "..")
$Solution   = Join-Path $RepoRoot "MssqlIntelliSense.slnx"
$DistDir    = Join-Path $RepoRoot "dist"
$ProjectDir = Join-Path $RepoRoot "src\MssqlIntelliSense.SsmsHost"

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  MSSQL IntelliSense - Installer Builder" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""

# 1. Build Solution
Write-Host "[1/4] Building solution ($Configuration)..." -ForegroundColor Yellow
& dotnet build $Solution --configuration $Configuration --verbosity minimal
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed. Aborting."
    exit 1
}
Write-Host "      Build succeeded." -ForegroundColor Green

# 2. Read Version
Write-Host ""
Write-Host "[2/4] Reading version manifest..." -ForegroundColor Yellow
$manifestPath = Join-Path $ProjectDir "source.extension.vsixmanifest"
$manifestContent = Get-Content $manifestPath -Raw
if ($manifestContent -match 'Identity\s[^>]*Version="(\d+\.\d+\.\d+)"') {
    $version = $Matches[1]
} else {
    $version = "0.2.73"
}
Write-Host "      Version: $version" -ForegroundColor Green

# 3. Create dist output directory and Portable Zip Package
Write-Host ""
Write-Host "[3/4] Creating Portable Zip Package..." -ForegroundColor Yellow
if (Test-Path $DistDir) {
    Remove-Item -Path $DistDir -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $DistDir -Force | Out-Null

$stagingDir = Join-Path $DistDir "staging"
$binDir     = Join-Path $stagingDir "bin"
New-Item -ItemType Directory -Path $binDir -Force | Out-Null

$ssmsBinSrc  = Join-Path $ProjectDir "bin\$Configuration\net472"
$debugBinSrc = Join-Path $RepoRoot "src\MssqlIntelliSense.DebugApp\bin\$Configuration\net472"

# Copy extension binaries
& robocopy $ssmsBinSrc $binDir /E /IS /IT /XF *.vsix /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
if (Test-Path $debugBinSrc) {
    & robocopy $debugBinSrc $binDir /E /IS /IT /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
}

# Copy installer scripts
Copy-Item (Join-Path $ScriptDir "install.ps1") (Join-Path $stagingDir "install.ps1") -Force
Copy-Item (Join-Path $ScriptDir "uninstall.ps1") (Join-Path $stagingDir "uninstall.ps1") -Force

# Create README.txt in staging
$readmeContent = @"
==================================================
  MSSQL IntelliSense v$version - Installation Guide
==================================================

Quick Installation:
1. Right-click 'install.ps1' and select 'Run with PowerShell'.
2. Or open PowerShell in this directory and run:
   .\install.ps1 -Launch

Features Installed:
- SSMS Extension (Auto-completes T-SQL, DDL Tooltips, AI Chat, Tool Lab)
- Standalone Debug Application (MssqlIntelliSense.DebugApp.exe)

To Uninstall:
- Run 'uninstall.ps1' with PowerShell.
"@
Set-Content -Path (Join-Path $stagingDir "README.txt") -Value $readmeContent

$zipPath = Join-Path $DistDir "MssqlIntelliSense-v$version-Portable.zip"
Compress-Archive -Path "$stagingDir\*" -DestinationPath $zipPath -Force
Remove-Item -Path $stagingDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "      Created: $zipPath" -ForegroundColor Green

# 4. Check for Inno Setup compiler to build .exe Setup
Write-Host ""
Write-Host "[4/4] Checking Inno Setup Compiler (iscc.exe)..." -ForegroundColor Yellow
$cmd = Get-Command "iscc.exe" -ErrorAction SilentlyContinue
$cmdPath = if ($cmd) { $cmd.Source } else { $null }
$isccPaths = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe",
    $cmdPath
)
$iscc = $isccPaths | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if ($iscc) {
    Write-Host "      Found Inno Setup Compiler: $iscc" -ForegroundColor Green
    $issFile = Join-Path $ScriptDir "installer.iss"
    if (Test-Path $issFile) {
        & $iscc /DAppVersion=$version /O"$DistDir" $issFile
        Write-Host "      Created Windows EXE Setup in dist/" -ForegroundColor Green
    }
} else {
    Write-Host "      ISCC.exe not installed. (Portable Zip package generated successfully)." -ForegroundColor Gray
}

Write-Host ""
Write-Host "Installer package build completed! Files generated in dist/:" -ForegroundColor Green
Get-ChildItem $DistDir | Select-Object Name, Length, LastWriteTime
