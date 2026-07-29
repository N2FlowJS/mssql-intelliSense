param (
    [string]$ProjectDir,
    [string]$TargetDir,
    [switch]$NoKill,
    [switch]$Launch
)

# Set defaults if not specified
if ([string]::IsNullOrEmpty($ProjectDir)) {
    $ProjectDir = Resolve-Path (Join-Path $PSScriptRoot "..\src\MssqlIntelliSense.SsmsHost")
}
if ([string]::IsNullOrEmpty($TargetDir)) {
    $TargetDir = Resolve-Path (Join-Path $ProjectDir "bin\Debug\net472")
}

Write-Host "Deploying SSMS Extension..." -ForegroundColor Cyan
Write-Host "Project directory: $ProjectDir"
Write-Host "Target directory:  $TargetDir"

# Kill SSMS processes by default to release assembly DLL locks
if (-not $NoKill) {
    Write-Host "Closing running SSMS processes..." -ForegroundColor Yellow
    $ssmsProcesses = Get-Process -Name "Ssms" -ErrorAction SilentlyContinue
    if ($ssmsProcesses) {
        $ssmsProcesses | Stop-Process -Force
        Start-Sleep -Seconds 2
    } else {
        Write-Host "No SSMS processes running." -ForegroundColor Gray
    }
}

# Locate SSMS AppData directories
$ssmsRoot = Join-Path $env:LOCALAPPDATA "Microsoft\SSMS"
$ssmsDirs = @()
if (Test-Path $ssmsRoot) {
    $ssmsDirs = Get-ChildItem $ssmsRoot -Directory | Where-Object { $_.Name -match "^22\." -or $_.Name -match "^20\." }
    if ($ssmsDirs.Count -eq 0) {
        $ssmsDirs = Get-ChildItem $ssmsRoot -Directory
    }
}

if ($ssmsDirs.Count -eq 0) {
    Write-Error "Could not locate SSMS AppData directory at $ssmsRoot."
    exit 1
}

# Deploy unpacked extension files to SSMS Extensions folder
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
            Write-Host "Removing legacy folder: $($legacy.FullName)" -ForegroundColor Yellow
            Remove-Item -Path $legacy.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }

        $installedFolders = Get-ChildItem -Path $extRoot -Directory | Where-Object {
            (Test-Path (Join-Path $_.FullName "MssqlIntelliSense.SsmsHost.dll")) -or
            (Test-Path (Join-Path $_.FullName "MssqlIntelliSense.SsmsHost.pkgdef"))
        } | Select-Object -ExpandProperty FullName

        $targetDirs = @($targetDirs + $installedFolders) | Select-Object -Unique
    }

    foreach ($targetDir in $targetDirs) {
        Write-Host "Deploying extension to: $targetDir" -ForegroundColor Yellow
        try {
            if (-not (Test-Path $targetDir)) {
                New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
            }

            Get-ChildItem -Path $targetDir -Recurse -File -ErrorAction SilentlyContinue | Where-Object {
                $_.Name -match 'SQLite|Sqlite|SourceGear|e_sqlite'
            } | Remove-Item -Force -ErrorAction SilentlyContinue

            # Copy build output files (excluding the .vsix archive itself)
            & robocopy $TargetDir $targetDir /E /IS /IT /XF *.vsix /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
            if ($LASTEXITCODE -gt 7) {
                throw "robocopy failed with exit code $LASTEXITCODE"
            }
            Write-Host "  Successfully deployed extension binaries." -ForegroundColor Green
            $deployed = $true
        }
        catch {
            Write-Warning "Failed to deploy to $targetDir`: $_"
        }
    }

    # Touch extensions.configurationchanged to notify SSMS extension engine
    $configChangedFile = Join-Path $extRoot "extensions.configurationchanged"
    New-Item -ItemType File -Path $configChangedFile -Force | Out-Null

    # Clear ComponentModelCache to force SSMS to rebuild MEF & Package cache clean
    $cacheDir = Join-Path $ssmsDir.FullName "ComponentModelCache"
    if (Test-Path $cacheDir) {
        Write-Host "  Clearing ComponentModelCache..." -ForegroundColor Gray
        Remove-Item -Path $cacheDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if (-not $deployed) {
    Write-Error "Deployment failed for all SSMS instances."
    exit 1
}

Write-Host "SSMS Extension deployment completed successfully!" -ForegroundColor Green

# Launch SSMS if requested
if ($Launch) {
    $ssmsPaths = @(
        "C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Ssms.exe",
        "C:\Program Files\Microsoft SQL Server Management Studio 22\Common7\IDE\Ssms.exe",
        "C:\Program Files (x86)\Microsoft SQL Server Management Studio 20\Common7\IDE\Ssms.exe",
        "C:\Program Files (x86)\Microsoft SQL Server Management Studio 19\Common7\IDE\Ssms.exe"
    )
    $ssmsExe = $ssmsPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($ssmsExe) {
        Write-Host "Launching SSMS: $ssmsExe" -ForegroundColor Green
        Start-Process $ssmsExe
    } else {
        Write-Warning "Could not locate Ssms.exe automatically. Please launch SSMS manually."
    }
}
