#Requires -Version 5.1
<#
.SYNOPSIS
    Build, deploy, launch SSMS and auto-attach Visual Studio debugger.

.DESCRIPTION
    1. Build MssqlIntelliSense.SsmsHost (Debug)
    2. Deploy extension files to SSMS LocalAppData
    2.5. Build & restart GraphQL Server (MssqlIntelliSense.GraphqlServer)
    3. Launch SSMS
    4. Auto-attach Visual Studio debugger to the new SSMS process

.PARAMETER Configuration
    Build configuration. Default: Debug

.PARAMETER NoKill
    Skip killing existing SSMS instances before launching.

.PARAMETER NoBuild
    Skip build step (use existing binaries).

.PARAMETER NoDeploy
    Skip deploy step.

.PARAMETER NoGraphQL
    Skip building and restarting the GraphQL Server.

.PARAMETER NoAttach
    Skip auto-attach debugger step (just launch SSMS).

.PARAMETER AttachWaitSec
    Seconds to wait for SSMS to initialize before attaching. Default: 8

.EXAMPLE
    .\scripts\debug-ssms.ps1
    .\scripts\debug-ssms.ps1 -NoBuild
    .\scripts\debug-ssms.ps1 -NoGraphQL
    .\scripts\debug-ssms.ps1 -NoAttach
#>
param (
    [string]$Configuration  = "Debug",
    [switch]$NoKill,
    [switch]$NoBuild,
    [switch]$NoDeploy,
    [switch]$NoAttach,
    [int]$AttachWaitSec     = 8
)

$ErrorActionPreference = "Stop"
$ScriptDir   = Split-Path $MyInvocation.MyCommand.Path
$RepoRoot    = Resolve-Path (Join-Path $ScriptDir "..")
$ProjectDir  = Join-Path $RepoRoot "src\MssqlIntelliSense.SsmsHost"
$ProjectFile = Join-Path $ProjectDir "MssqlIntelliSense.SsmsHost.csproj"

# No GraphQL project properties

Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  MssqlIntelliSense SSMS Debug Launcher" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""

# Pre-build checks (GraphQL server shutdown removed)

# ── 1. BUILD SSMS EXTENSION ───────────────────────────────────────────────────
if (-not $NoBuild) {
    Write-Host "[1/5] Building SSMS extension ($Configuration)..." -ForegroundColor Yellow
    & dotnet build $ProjectFile `
        --configuration $Configuration `
        --verbosity minimal `
        /p:DisableSsmsDeploy=true
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed (exit code $LASTEXITCODE). Aborting."
        exit 1
    }
    Write-Host "      Build OK" -ForegroundColor Green
} else {
    Write-Host "[1/5] Build skipped (-NoBuild)" -ForegroundColor Gray
}

# ── 2. DEPLOY ─────────────────────────────────────────────────────────────────
if (-not $NoDeploy) {
    Write-Host ""
    Write-Host "[2/5] Deploying extension to SSMS..." -ForegroundColor Yellow
    $deployScript = Join-Path $ScriptDir "deploy-ssms.ps1"
    $targetDir    = Join-Path $ProjectDir "bin\$Configuration\net472"
    & powershell -NoProfile -ExecutionPolicy Bypass -File $deployScript `
        -ProjectDir $ProjectDir `
        -TargetDir  $targetDir `
        $(if (-not $NoKill) { "-Kill" })
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Deploy failed. Aborting."
        exit 1
    }
    Write-Host "      Deploy OK" -ForegroundColor Green
} else {
    Write-Host "[2/5] Deploy skipped (-NoDeploy)" -ForegroundColor Gray
}

# GraphQL Server build & restart steps removed

# ── 3. LAUNCH SSMS ────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "[4/5] Launching SSMS..." -ForegroundColor Yellow

$ssmsPaths = @(
    "C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Ssms.exe",
    "C:\Program Files\Microsoft SQL Server Management Studio 22\Common7\IDE\Ssms.exe",
    "C:\Program Files (x86)\Microsoft SQL Server Management Studio 20\Common7\IDE\Ssms.exe",
    "C:\Program Files (x86)\Microsoft SQL Server Management Studio 19\Common7\IDE\Ssms.exe"
)

$ssmsExe = $ssmsPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $ssmsExe) {
    Write-Warning "Cannot find Ssms.exe. Please launch SSMS manually."
    exit 0
}

Write-Host "      Path : $ssmsExe" -ForegroundColor Gray
$ssmsProc = Start-Process -FilePath $ssmsExe -PassThru
Write-Host "      PID  : $($ssmsProc.Id)" -ForegroundColor Gray
Write-Host "      SSMS launched!" -ForegroundColor Green

# ── 4. AUTO-ATTACH VS DEBUGGER ────────────────────────────────────────────────
if (-not $NoAttach) {
    Write-Host ""
    Write-Host "[5/5] Auto-attaching Visual Studio debugger..." -ForegroundColor Yellow
    Write-Host "      Waiting ${AttachWaitSec}s for SSMS to initialize..." -ForegroundColor Gray
    Start-Sleep -Seconds $AttachWaitSec

    # Register the VisualStudioAttacher C# class using Add-Type if not already present
    if (-not ([System.Management.Automation.PSTypeName]"VisualStudioAttacher").Type) {
        $csharpCode = @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

public class VisualStudioAttacher {
    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable prot);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx ppbc);

    public static List<object> GetDTEs() {
        List<object> dtes = new List<object>();
        IRunningObjectTable rot = null;
        IEnumMoniker enumMoniker = null;
        try {
            if (GetRunningObjectTable(0, out rot) != 0 || rot == null) return dtes;
            rot.EnumRunning(out enumMoniker);
            enumMoniker.Reset();
            IMoniker[] moniker = new IMoniker[1];
            IntPtr fetched = IntPtr.Zero;
            while (enumMoniker.Next(1, moniker, fetched) == 0) {
                IBindCtx bindCtx = null;
                try {
                    CreateBindCtx(0, out bindCtx);
                    string displayName;
                    moniker[0].GetDisplayName(bindCtx, null, out displayName);
                    if (displayName.StartsWith("!VisualStudio.DTE")) {
                        object dte;
                        rot.GetObject(moniker[0], out dte);
                        if (dte != null) {
                            dtes.Add(dte);
                        }
                    }
                } catch {
                    // Ignore
                } finally {
                    if (bindCtx != null) Marshal.ReleaseComObject(bindCtx);
                }
            }
        } catch {
            // Ignore
        } finally {
            if (enumMoniker != null) Marshal.ReleaseComObject(enumMoniker);
            if (rot != null) Marshal.ReleaseComObject(rot);
        }
        return dtes;
    }
}
"@
        Add-Type -TypeDefinition $csharpCode -ErrorAction SilentlyContinue | Out-Null
    }

    $attached = $false
    $dtes = @()
    try {
        $dtes = [VisualStudioAttacher]::GetDTEs()
    } catch {
        Write-Warning "Could not enumerate Running Object Table: $_"
    }

    Write-Host "      Found $($dtes.Count) running Visual Studio instances." -ForegroundColor Gray

    # Try to find the VS instance that has MssqlIntelliSense solution open
    $targetDte = $null
    foreach ($dte in $dtes) {
        try {
            $sol = $dte.Solution
            if ($sol -and $sol.FullName -like "*MssqlIntelliSense*") {
                $targetDte = $dte
                Write-Host "      Selected VS instance with open solution: $($sol.FullName)" -ForegroundColor Gray
                break
            }
        } catch {
            # Some DTE instances might be busy or inaccessible
        }
    }

    # Fallback to the first available if not found
    if (-not $targetDte -and $dtes.Count -gt 0) {
        $targetDte = $dtes[0]
        Write-Host "      No VS instance with MssqlIntelliSense solution found. Using the first VS instance." -ForegroundColor Gray
    }

    if ($targetDte) {
        try {
            $dbg = $targetDte.Debugger
            $procs = $dbg.LocalProcesses

            foreach ($p in $procs) {
                if ($p.Name -like "*Ssms.exe" -and $p.ProcessID -eq $ssmsProc.Id) {
                    Write-Host "      Attaching to PID $($p.ProcessID)..." -ForegroundColor Gray
                    $p.Attach()
                    $attached = $true
                    Write-Host "      Debugger attached!" -ForegroundColor Green
                    break
                }
            }
        } catch {
            Write-Warning "Failed to attach via selected VS instance: $_"
        }
    }

    if (-not $attached) {
        Write-Host ""
        Write-Warning "Could not auto-attach (VS not running or DTE unavailable)."
        Write-Host "  Manual attach:" -ForegroundColor Yellow
        Write-Host "    Debug > Attach to Process > Ssms.exe (PID $($ssmsProc.Id))" -ForegroundColor White
    }
} else {
    Write-Host ""
    Write-Host "[5/5] Attach skipped (-NoAttach)" -ForegroundColor Gray
    Write-Host "  To attach manually:" -ForegroundColor White
    Write-Host "    Debug > Attach to Process > Ssms.exe (PID $($ssmsProc.Id))" -ForegroundColor White
}

Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  Done! SSMS PID = $($ssmsProc.Id)" -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""
