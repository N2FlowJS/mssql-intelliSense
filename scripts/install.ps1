#Requires -Version 5.1
<#
.SYNOPSIS
    Standard VSIX installer/updater for MSSQL IntelliSense SSMS extension.

.DESCRIPTION
    Builds on SSMS/Visual Studio's VSIXInstaller instead of copying extension
    binaries into the SSMS Extensions folder. The same command installs a new
    extension or updates an existing one when the VSIX version is newer.

.PARAMETER VsixPath
    Path to the VSIX package. Defaults to the Release VSIX output.

.PARAMETER Launch
    Automatically launch SSMS after installation completes.

.PARAMETER NoKill
    Skip killing existing SSMS processes before installing.

.EXAMPLE
    .\scripts\install.ps1
    .\scripts\install.ps1 -VsixPath .\src\MssqlIntelliSense.SsmsHost\bin\Release\net472\MssqlIntelliSense.SsmsHost.vsix
    .\scripts\install.ps1 -Launch
#>
param (
    [string]$VsixPath = "",
    [switch]$Launch,
    [switch]$NoKill
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path $MyInvocation.MyCommand.Path
$RepoRoot  = Resolve-Path (Join-Path $ScriptDir "..")

. (Join-Path $ScriptDir "shared.ps1")

function Get-LocalVsixInstaller {
    $candidates = @(
        "C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe",
        "C:\Program Files\Microsoft SQL Server Management Studio 22\Common7\IDE\VSIXInstaller.exe",
        "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\VSIXInstaller.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\VSIXInstaller.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Professional\Common7\IDE\VSIXInstaller.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\VSIXInstaller.exe",
        "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\Common7\IDE\VSIXInstaller.exe"
    )

    return $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

function Get-VsixIdentity {
    param([Parameter(Mandatory)][string]$Path)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -ieq "extension.vsixmanifest" } | Select-Object -First 1
        if (-not $entry) {
            throw "VSIX manifest not found in $Path"
        }

        $reader = New-Object System.IO.StreamReader($entry.Open())
        try {
            $content = $reader.ReadToEnd()
        } finally {
            $reader.Dispose()
        }

        if ($content -notmatch 'Identity\s+[^>]*Id="([^"]+)"[^>]*Version="([^"]+)"') {
            throw "Could not parse VSIX identity/version from $Path"
        }

        return [pscustomobject]@{
            Id      = $Matches[1]
            Version = $Matches[2]
        }
    } finally {
        $zip.Dispose()
    }
}

function Test-InstalledVsix {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$VsixInstallerPath
    )

    $candidateRoots = @()
    $installerIdeDir = Split-Path $VsixInstallerPath
    $candidateRoots += Join-Path $installerIdeDir "Extensions"

    $ssmsExe = Get-SsmsExecutable
    if ($ssmsExe) {
        $candidateRoots += Join-Path (Split-Path $ssmsExe) "Extensions"
    }

    foreach ($root in ($candidateRoots | Select-Object -Unique)) {
        if (-not (Test-Path $root)) { continue }
        $manifests = Get-ChildItem -Path $root -Filter "extension.vsixmanifest" -Recurse -ErrorAction SilentlyContinue
        foreach ($manifest in $manifests) {
            $content = Get-Content -Path $manifest.FullName -Raw -ErrorAction SilentlyContinue
            if ($content -match ('Id="' + [regex]::Escape($Id) + '"') -and $content -match ('Version="' + [regex]::Escape($Version) + '"')) {
                return $manifest.Directory.FullName
            }
        }
    }

    return $null
}

function Remove-LegacyAppDataExtensions {
    param([Parameter(Mandatory)][string]$Id)

    $root = Join-Path $env:LOCALAPPDATA "Microsoft\SSMS"
    if (-not (Test-Path $root)) { return @() }

    $rootFullPath = [System.IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
    $removed = @()
    $ssmsDirs = Get-SsmsDirectories -Root $root
    foreach ($ssmsDir in $ssmsDirs) {
        $extRoot = Join-Path $ssmsDir.FullName "Extensions"
        if (-not (Test-Path $extRoot)) { continue }

        $legacyDirs = Get-ChildItem -Path $extRoot -Directory -ErrorAction SilentlyContinue | Where-Object {
            $dllPath = Join-Path $_.FullName "MssqlIntelliSense.SsmsHost.dll"
            $manifestPath = Join-Path $_.FullName "extension.vsixmanifest"
            $hasDll = Test-Path $dllPath
            $hasManifestId = $false
            if (Test-Path $manifestPath) {
                $manifestContent = Get-Content -Path $manifestPath -Raw -ErrorAction SilentlyContinue
                $hasManifestId = $manifestContent -match ('Id="' + [regex]::Escape($Id) + '"')
            }
            $_.Name -eq "MssqlIntelliSense.SsmsHost" -or $hasDll -or $hasManifestId
        }

        foreach ($legacyDir in $legacyDirs) {
            $fullPath = [System.IO.Path]::GetFullPath($legacyDir.FullName).TrimEnd('\') + '\'
            if (-not $fullPath.StartsWith($rootFullPath, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing to remove path outside SSMS AppData root: $fullPath"
            }

            Remove-Item -LiteralPath $legacyDir.FullName -Recurse -Force -ErrorAction Stop
            $removed += $legacyDir.FullName
        }
    }

    return $removed
}

if ([string]::IsNullOrWhiteSpace($VsixPath)) {
    $localVsix = Join-Path $ScriptDir "MssqlIntelliSense.SsmsHost.vsix"
    if (Test-Path $localVsix) {
        $VsixPath = $localVsix
    } else {
        $VsixPath = Join-Path $RepoRoot "src\MssqlIntelliSense.SsmsHost\bin\Release\net472\MssqlIntelliSense.SsmsHost.vsix"
    }
}

$VsixPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($VsixPath)
if (-not (Test-Path $VsixPath)) {
    Write-Error "VSIX not found: $VsixPath. Build Release first: dotnet build src\MssqlIntelliSense.SsmsHost\MssqlIntelliSense.SsmsHost.csproj -c Release"
    exit 1
}

$vsixInstaller = Get-LocalVsixInstaller
if (-not $vsixInstaller) {
    Write-Error "Could not locate VSIXInstaller.exe for SSMS/Visual Studio."
    exit 1
}
$vsixIdentity = Get-VsixIdentity -Path $VsixPath

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  MSSQL IntelliSense - VSIX Installer" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "VSIX:      $VsixPath" -ForegroundColor Gray
Write-Host "Installer: $vsixInstaller" -ForegroundColor Gray
Write-Host "Extension: $($vsixIdentity.Id) $($vsixIdentity.Version)" -ForegroundColor Gray
Write-Host ""

$alreadyInstalledPath = Test-InstalledVsix -Id $vsixIdentity.Id -Version $vsixIdentity.Version -VsixInstallerPath $vsixInstaller
$skipInstall = -not [string]::IsNullOrWhiteSpace($alreadyInstalledPath)

Write-Host "[1/3] Closing running SSMS processes..." -ForegroundColor Yellow
$stopped = Stop-SsmsProcesses -Skip:$NoKill
$staleInstallers = Get-Process -Name "VSIXInstaller" -ErrorAction SilentlyContinue
if (-not $NoKill -and $staleInstallers) {
    $staleInstallers | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    $stopped = $true
}
if ($stopped) {
    Write-Host "      Blocking SSMS/installer processes closed." -ForegroundColor Green
} else {
    Write-Host "      No running SSMS/installer process found." -ForegroundColor Gray
}

Write-Host ""
Write-Host "[2/3] Installing/updating VSIX..." -ForegroundColor Yellow
if ($skipInstall) {
    $installedPath = $alreadyInstalledPath
    Write-Host "      Already installed at: $installedPath" -ForegroundColor Green
} else {
    $installProcess = Start-Process -FilePath $vsixInstaller -ArgumentList @("/a", "/s", $VsixPath) -PassThru -NoNewWindow
    $completed = $installProcess.WaitForExit(600000)
    if (-not $completed) {
        $installedPath = Test-InstalledVsix -Id $vsixIdentity.Id -Version $vsixIdentity.Version -VsixInstallerPath $vsixInstaller
        if ($installedPath) {
            Write-Warning "VSIXInstaller did not exit after commit; installed extension was verified at $installedPath."
            Stop-Process -Id $installProcess.Id -Force -ErrorAction SilentlyContinue
            Get-Process -Name "VSIXInstaller" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        } else {
            Stop-Process -Id $installProcess.Id -Force -ErrorAction SilentlyContinue
            Write-Error "VSIXInstaller timed out before the extension could be verified."
            exit 1
        }
    } elseif ($installProcess.ExitCode -ne 0) {
        Write-Error "VSIXInstaller failed with exit code $($installProcess.ExitCode)."
        exit $installProcess.ExitCode
    } else {
        $installedPath = Test-InstalledVsix -Id $vsixIdentity.Id -Version $vsixIdentity.Version -VsixInstallerPath $vsixInstaller
    }

    if (-not $installedPath) {
        Write-Error "VSIXInstaller finished, but $($vsixIdentity.Id) $($vsixIdentity.Version) was not found in SSMS Extensions."
        exit 1
    }
    Write-Host "      Installed/updated at: $installedPath" -ForegroundColor Green
}

Write-Host ""
Write-Host "[3/3] Refreshing SSMS caches..." -ForegroundColor Yellow
$removedLegacyDirs = Remove-LegacyAppDataExtensions -Id $vsixIdentity.Id
foreach ($legacyDir in $removedLegacyDirs) {
    Write-Host "      Removed legacy AppData extension: $legacyDir" -ForegroundColor Gray
}

$ssmsDirs = Get-SsmsDirectories
foreach ($ssmsDir in $ssmsDirs) {
    $extRoot = Join-Path $ssmsDir.FullName "Extensions"
    if (Test-Path $extRoot) {
        $configChangedFile = Join-Path $extRoot "extensions.configurationchanged"
        New-Item -ItemType File -Path $configChangedFile -Force | Out-Null
    }

    if (Clear-ComponentModelCache -SsmsDir $ssmsDir.FullName) {
        Write-Host "      Cleared ComponentModelCache for $($ssmsDir.Name)." -ForegroundColor Gray
    }
}

Write-Host ""
Write-Host "Installation/update completed successfully." -ForegroundColor Green

if ($Launch) {
    Write-Host ""
    Write-Host "Launching SSMS..." -ForegroundColor Cyan
    $ssmsExe = Get-SsmsExecutable
    if ($ssmsExe) {
        Start-Process -FilePath $ssmsExe
    } else {
        Write-Warning "Could not locate Ssms.exe automatically. Please launch SSMS manually."
    }
}
