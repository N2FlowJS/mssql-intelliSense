#Requires -Version 5.1
<#
.SYNOPSIS
    Shared utility functions for MSSQL IntelliSense scripts.
.DESCRIPTION
    Provides common functions for SSMS detection, process management,
    cache clearing, version parsing, and file deployment.
#>

function Get-SsmsDirectories {
    <#
    .SYNOPSIS
        Returns SSMS AppData version directories (22.x, 20.x, 19.x, 18.x).
    .PARAMETER Root
        Override the default SSMS AppData root path.
    #>
    [CmdletBinding()]
    param([string]$Root = "")

    if ([string]::IsNullOrEmpty($Root)) {
        $Root = Join-Path $env:LOCALAPPDATA "Microsoft\SSMS"
    }

    if (-not (Test-Path $Root)) {
        return @()
    }

    $dirs = Get-ChildItem $Root -Directory |
            Where-Object { $_.Name -match "^(22|20|19|18)\." }

    if ($dirs.Count -eq 0) {
        $dirs = Get-ChildItem $Root -Directory
    }

    return $dirs
}

function Stop-SsmsProcesses {
    <#
    .SYNOPSIS
        Stops all running SSMS processes.
    .PARAMETER Skip
        Skip killing processes.
    .OUTPUTS
        $true if processes were killed, $false if skipped or none found.
    #>
    [CmdletBinding()]
    param([switch]$Skip)

    if ($Skip) { return $false }

    $procs = Get-Process -Name "Ssms" -ErrorAction SilentlyContinue
    if ($procs) {
        $procs | Stop-Process -Force
        Start-Sleep -Seconds 2
        return $true
    }
    return $false
}

function Clear-ComponentModelCache {
    <#
    .SYNOPSIS
        Removes the ComponentModelCache directory for a given SSMS version folder.
    .PARAMETER SsmsDir
        The SSMS version directory (e.g. 22.0.0.0).
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$SsmsDir)

    $cacheDir = Join-Path $SsmsDir "ComponentModelCache"
    if (Test-Path $cacheDir) {
        Remove-Item -Path $cacheDir -Recurse -Force -ErrorAction SilentlyContinue
        return $true
    }
    return $false
}

function Remove-LegacySqliteFiles {
    <#
    .SYNOPSIS
        Removes legacy SQLite/SourceGear DLLs from the target directory.
    .PARAMETER Path
        The directory to clean.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    Get-ChildItem -Path $Path -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match 'SQLite|Sqlite|SourceGear|e_sqlite' } |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

function Get-SsmsExecutable {
    <#
    .SYNOPSIS
        Returns the first found Ssms.exe path on the system.
    #>
    [CmdletBinding()]
    param()

    $paths = @(
        "C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Ssms.exe",
        "C:\Program Files\Microsoft SQL Server Management Studio 22\Common7\IDE\Ssms.exe",
        "C:\Program Files (x86)\Microsoft SQL Server Management Studio 20\Common7\IDE\Ssms.exe",
        "C:\Program Files (x86)\Microsoft SQL Server Management Studio 19\Common7\IDE\Ssms.exe",
        "C:\Program Files (x86)\Microsoft SQL Server Management Studio 18\Common7\IDE\Ssms.exe"
    )

    return $paths | Where-Object { Test-Path $_ } | Select-Object -First 1
}

function Invoke-Robocopy {
    <#
    .SYNOPSIS
        Wrapper around robocopy with consistent error handling.
    .PARAMETER Source
        Source directory.
    .PARAMETER Destination
        Destination directory.
    .PARAMETER ExcludeFiles
        File patterns to exclude (default: *.vsix).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [string[]]$ExcludeFiles = @("*.vsix")
    )

    $excludeArgs = @()
    foreach ($pattern in $ExcludeFiles) {
        $excludeArgs += "/XF"
        $excludeArgs += $pattern
    }

    & robocopy $Source $Destination /E /IS /IT @excludeArgs /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null

    if ($LASTEXITCODE -gt 7) {
        throw "robocopy failed with exit code $LASTEXITCODE"
    }
}

function Get-VsixVersion {
    <#
    .SYNOPSIS
        Parses the version string from source.extension.vsixmanifest.
    .PARAMETER ManifestPath
        Full path to the vsixmanifest file.
    .OUTPUTS
        The version string (e.g. "1.2.3") or $null if parsing fails.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ManifestPath)

    $content = Get-Content $ManifestPath -Raw
    if ($content -match 'Identity\s[^>]*Version="(\d+\.\d+\.\d+)"') {
        return $Matches[1]
    }
    return $null
}
