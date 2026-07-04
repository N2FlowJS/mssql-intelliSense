param (
    [string]$ProjectDir,
    [string]$TargetDir,
    [switch]$Kill,
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

# Kill SSMS if requested
if ($Kill) {
    Write-Host "Checking for running SSMS instances..." -ForegroundColor Yellow
    $ssmsProcesses = Get-Process -Name "Ssms" -ErrorAction SilentlyContinue
    if ($ssmsProcesses) {
        Write-Host "Closing running SSMS processes..." -ForegroundColor Yellow
        $ssmsProcesses | Stop-Process -Force
        Start-Sleep -Seconds 2
    } else {
        Write-Host "No SSMS processes running." -ForegroundColor Gray
    }
}

# Locate VSIXInstaller.exe from SSMS installation
$vsixInstallerPaths = @(
    "C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe",
    "C:\Program Files\Microsoft SQL Server Management Studio 22\Common7\IDE\VSIXInstaller.exe",
    "C:\Program Files (x86)\Microsoft SQL Server Management Studio 20\Common7\IDE\VSIXInstaller.exe"
)
$vsixInstaller = $vsixInstallerPaths | Where-Object { Test-Path $_ } | Select-Object -First 1

# Locate the built .vsix file
$vsixFile = Join-Path $TargetDir "MssqlIntelliSense.SsmsHost.vsix"
if (-not (Test-Path $vsixFile)) {
    Write-Error "VSIX file not found at: $vsixFile`nMake sure you built the project first."
    exit 1
}
Write-Host "VSIX file: $vsixFile" -ForegroundColor Gray

# Locate SSMS AppData directories
$ssmsRoot = Join-Path $env:LOCALAPPDATA "Microsoft\SSMS"
$ssmsDirs = @()
if (Test-Path $ssmsRoot) {
    $ssmsDirs = Get-ChildItem $ssmsRoot -Directory | Where-Object { $_.Name -match "^22\." -or $_.Name -match "^20\." }
    if ($ssmsDirs.Count -eq 0) {
        $ssmsDirs = Get-ChildItem $ssmsRoot -Directory
    }
}

$deployed = $false

# Install via VSIXInstaller
if ($vsixInstaller) {
    Write-Host "Using VSIXInstaller: $vsixInstaller" -ForegroundColor Gray

    # Clean up conflicting manually deployed folders under LocalAppData
    foreach ($ssmsDir in $ssmsDirs) {
        $manualDir = Join-Path $ssmsDir.FullName "Extensions\MssqlIntelliSense.SsmsHost"
        if (Test-Path $manualDir) {
            Write-Host "Removing manual deploy folder to prevent conflict: $manualDir" -ForegroundColor Yellow
            try { Remove-Item -Path $manualDir -Recurse -Force -ErrorAction Stop }
            catch { Write-Warning "Could not delete $manualDir. Please close SSMS." }
        }
    }

    $extensionId = "MssqlIntelliSense.Ssms22"
    Write-Host "Uninstalling old VSIX version (ID: $extensionId)..." -ForegroundColor Yellow
    $uninstallProc = Start-Process -FilePath $vsixInstaller -ArgumentList "/q /u:$extensionId" `
        -Wait -PassThru -NoNewWindow
    if ($uninstallProc.ExitCode -eq 0) {
        Write-Host "  Old version uninstalled." -ForegroundColor Gray
    } else {
        Write-Host "  Not previously installed or already removed - continuing." -ForegroundColor Gray
    }

    Write-Host "Installing new VSIX..." -ForegroundColor Yellow
    $installProc = Start-Process -FilePath $vsixInstaller -ArgumentList "/q `"$vsixFile`"" `
        -Wait -PassThru -NoNewWindow
    if ($installProc.ExitCode -eq 0) {
        Write-Host "Successfully installed via VSIXInstaller!" -ForegroundColor Green
        $deployed = $true

        # Touch extensions.configurationchanged to force SSMS to reload the VSIX configuration
        foreach ($ssmsDir in $ssmsDirs) {
            $extDir = Join-Path $ssmsDir.FullName "Extensions"
            if (Test-Path $extDir) {
                $configChangedFile = Join-Path $extDir "extensions.configurationchanged"
                New-Item -ItemType File -Path $configChangedFile -Force | Out-Null
            }
        }
    } else {
        Write-Warning "VSIXInstaller exited with code $($installProc.ExitCode). Falling back to manual copy..."
    }
}

# Fallback: manual file copy into SSMS Extensions directory
if (-not $deployed) {
    $extensionName = "MssqlIntelliSense.SsmsHost"

    # Find SSMS Extensions root from known locations
    $ssmsExtensionRoots = @()
    foreach ($ssmsDir in $ssmsDirs) {
        $extRoot = Join-Path $ssmsDir.FullName "Extensions"
        if (Test-Path $extRoot) {
            $ssmsExtensionRoots += $extRoot
        }
    }

    # Also check common SSMS 22 IDE Extensions paths
    $additionalPaths = @(
        "C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Extensions",
        "C:\Program Files\Microsoft SQL Server Management Studio 22\Common7\IDE\Extensions"
    )
    foreach ($p in $additionalPaths) {
        if ((Test-Path $p) -and ($ssmsExtensionRoots -notcontains $p)) {
            $ssmsExtensionRoots += $p
        }
    }

    if ($ssmsExtensionRoots.Count -eq 0) {
        Write-Warning "Could not locate SSMS Extensions directory. Manual copy skipped."
        Write-Host "VSIX built at: $vsixFile" -ForegroundColor Cyan
        Write-Host "Please install manually by double-clicking the VSIX file." -ForegroundColor Yellow
        # Don't fail the build - VSIX was built successfully
        exit 0
    }

    foreach ($extRoot in $ssmsExtensionRoots) {
        $destDir = Join-Path $extRoot $extensionName
        Write-Host "Copying extension files to: $destDir" -ForegroundColor Yellow

        try {
            New-Item -ItemType Directory -Path $destDir -Force | Out-Null
            # Copy all built files, excluding the VSIX itself
            Copy-Item -Path (Join-Path $TargetDir "*") -Destination $destDir -Recurse -Force -Exclude "*.vsix"
            Write-Host "  Copied successfully." -ForegroundColor Green
            $deployed = $true

            # Touch extensions.configurationchanged so SSMS reloads on next start
            $configChangedFile = Join-Path $extRoot "extensions.configurationchanged"
            New-Item -ItemType File -Path $configChangedFile -Force | Out-Null
        }
        catch {
            Write-Warning "Failed to copy to $destDir`: $_"
        }
    }

    if ($deployed) {
        Write-Host "Manual deploy completed successfully." -ForegroundColor Green
    } else {
        Write-Host "VSIX built at: $vsixFile" -ForegroundColor Cyan
        Write-Host "Could not auto-deploy. Please install manually." -ForegroundColor Yellow
        # Exit 0 so MSBuild does not fail - build artifact was produced correctly
        exit 0
    }
}

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
