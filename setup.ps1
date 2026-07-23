<#
  StockPicker - setup & health check.

  Written for people who are NOT programmers. It checks for the free Microsoft
  .NET 8 toolkit (installs it automatically if missing), builds the app, and
  runs a quick self-test so you KNOW it works. Launched by setup.cmd.

  Why a .cmd wrapper instead of running this directly: double-clicking a .ps1
  file opens it in Notepad, so setup.cmd is the thing the user double-clicks and
  it hands off to this script with the execution policy relaxed for one run.

  Safe to re-run anytime. Targets Windows PowerShell 5.1 (the version every
  Windows 10/11 PC already has), so no modern-only syntax is used here.
#>

# Stop on real (cmdlet) errors so a failure can't be silently skipped. Native
# program exit codes are checked explicitly below; PS 5.1 does not turn a
# native tool's stderr into a terminating error, which is exactly what we want.
$ErrorActionPreference = 'Stop'

$RepoRoot      = $PSScriptRoot
$Solution      = Join-Path $RepoRoot 'StockPicker.sln'
$CliProject    = Join-Path $RepoRoot 'StockPicker.Cli\StockPicker.Cli.csproj'
$CliDll        = Join-Path $RepoRoot 'StockPicker.Cli\bin\Release\net8.0\stockpicker.dll'
$DesktopExe    = Join-Path $RepoRoot 'StockPicker.Desktop\bin\Release\net8.0\StockPicker.exe'
$DotnetDownload = 'https://dotnet.microsoft.com/en-us/download/dotnet/8.0'

# ── Plain-English output helpers ───────────────────────────────────────────
function Write-Title($text) {
    Write-Host ''
    Write-Host ('=' * 70) -ForegroundColor DarkCyan
    Write-Host "  $text" -ForegroundColor Cyan
    Write-Host ('=' * 70) -ForegroundColor DarkCyan
}
function Write-Step($text) { Write-Host ''; Write-Host ">> $text" -ForegroundColor White }
function Write-Ok($text)   { Write-Host "   [OK] $text"   -ForegroundColor Green }
function Write-Note($text) { Write-Host "   $text"        -ForegroundColor Gray }
function Write-Warn($text) { Write-Host "   [!]  $text"   -ForegroundColor Yellow }
function Write-Fail($text) { Write-Host "   [X]  $text"   -ForegroundColor Red }

# ── Find a usable .NET 8 SDK, or return $null ──────────────────────────────
# Returns the full path to dotnet.exe only if an 8.x SDK is actually installed
# (an older SDK alone would let the build fail with a confusing message).
function Get-DotnetPath {
    $candidates = @()
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $candidates += $cmd.Source }
    $candidates += 'C:\Program Files\dotnet\dotnet.exe'

    foreach ($exe in $candidates) {
        if ($exe -and (Test-Path $exe)) {
            try {
                $sdks = & $exe --list-sdks 2>$null
                if ($sdks | Where-Object { $_ -match '^\s*8\.' }) { return $exe }
            } catch { }
        }
    }
    return $null
}

# ── Install the .NET 8 SDK for the user ────────────────────────────────────
# Prefers winget (built into Windows 10/11). Falls back to opening the official
# download page if winget is unavailable or the silent install fails.
function Install-Dotnet {
    Write-Step 'The .NET 8 toolkit is not installed yet. Installing it now...'
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget) {
        Write-Note 'Using the built-in Windows Package Manager (winget).'
        Write-Note 'If Windows asks "Do you want to allow changes?", click Yes.'
        try {
            & winget install --id Microsoft.DotNet.SDK.8 -e --source winget `
                --accept-package-agreements --accept-source-agreements
        } catch {
            Write-Warn "Automatic install hit a snag: $($_.Exception.Message)"
        }
        $dn = Get-DotnetPath
        if ($dn) {
            Write-Ok '.NET 8 installed successfully.'
            return $dn
        }
    } else {
        Write-Warn 'Windows Package Manager (winget) is not available on this PC.'
    }

    # Manual fallback - open the download page in the browser.
    Write-Warn 'Could not install .NET 8 automatically.'
    Write-Note 'Opening the official download page in your web browser...'
    Write-Note "  $DotnetDownload"
    Write-Note 'On that page, click the ".NET 8.0" SDK download for Windows x64,'
    Write-Note 'install it, then double-click setup again.'
    try { Start-Process $DotnetDownload } catch { }
    return $null
}

# ── Run a native command and stop with a clear message if it fails ─────────
function Invoke-Checked($exe, $arguments, $friendlyName) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "$friendlyName failed (error code $LASTEXITCODE)."
        Write-Note 'The lines just above this one explain what went wrong.'
        throw "$friendlyName failed."
    }
}

# ═══════════════════════════════════════════════════════════════════════════
#  MAIN
# ═══════════════════════════════════════════════════════════════════════════
try {
    Write-Title 'StockPicker Setup'
    Write-Note  'This will install what is needed, build the app, and test it.'
    Write-Note  'It is safe to run again at any time.'

    # 1. Make sure the .NET 8 toolkit is available --------------------------
    Write-Step 'Checking for the .NET 8 toolkit...'
    $dotnet = Get-DotnetPath
    if ($dotnet) {
        Write-Ok 'Found it.'
    } else {
        $dotnet = Install-Dotnet
        if (-not $dotnet) {
            Write-Host ''
            Write-Fail 'Setup stopped: the .NET 8 toolkit still is not installed.'
            Write-Note 'Install it from the page that just opened, then run setup again.'
            exit 1
        }
    }

    # 2. Download the app's building blocks ---------------------------------
    Write-Step 'Getting everything the app needs (this can take a minute)...'
    Invoke-Checked $dotnet @('restore', $Solution) 'Download step'
    Write-Ok 'Done.'

    # 3. Build the app ------------------------------------------------------
    Write-Step 'Building the app...'
    Invoke-Checked $dotnet @('build', $Solution, '-c', 'Release', '--nologo') 'Build step'
    Write-Ok 'The app built with no errors.'

    # 4. Self-test: prove the engine runs (offline, instant) ----------------
    Write-Step 'Running a quick self-test...'
    if (-not (Test-Path $CliDll)) {
        Write-Fail 'Self-test could not find the program that was just built.'
        throw 'Built program not found.'
    }
    $testOutput = & $dotnet $CliDll strategies
    if ($LASTEXITCODE -eq 0 -and ($testOutput -join "`n") -match 'momentum') {
        Write-Ok 'Self-test passed - the recommendation engine works.'
    } else {
        Write-Fail "Self-test did not pass (error code $LASTEXITCODE)."
        throw 'Self-test failed.'
    }

    # 5. Bonus: live-data test (needs internet; never blocks success) -------
    Write-Step 'Optional: testing live market data (needs internet)...'
    try {
        $job = Start-Job -ScriptBlock {
            param($dn, $dll)
            & $dn $dll scan --strategy momentum --index dow30 --limit 3 --top 3 2>$null | Out-Null
            $LASTEXITCODE
        } -ArgumentList $dotnet, $CliDll

        if (Wait-Job $job -Timeout 90) {
            $code = Receive-Job $job
            if ($code -eq 0) { Write-Ok 'Live market data is reachable.' }
            else { Write-Warn 'Could not fetch live data right now (the app still works offline-tested).' }
        } else {
            Stop-Job $job -ErrorAction SilentlyContinue
            Write-Warn 'Live-data test timed out - skipping (no internet?). The app still works.'
        }
        Remove-Job $job -Force -ErrorAction SilentlyContinue
    } catch {
        Write-Warn 'Skipped the live-data test. This does not affect whether the app works.'
    }

    # 6. Success summary + how to use it ------------------------------------
    Write-Title 'All set - StockPicker is installed and working!'
    Write-Host ''
    Write-Host '  HOW TO USE IT:' -ForegroundColor White
    Write-Host ''
    if (Test-Path $DesktopExe) {
        Write-Host '   Desktop app (windows, charts, tabs):' -ForegroundColor White
        Write-Host "     Double-click:  $DesktopExe" -ForegroundColor Cyan
        Write-Host ''
    }
    Write-Host '   Command-line tool (quick text results):' -ForegroundColor White
    Write-Host '     Open a terminal in this folder and run, for example:' -ForegroundColor Gray
    Write-Host "       dotnet `"$CliDll`" strategies" -ForegroundColor Cyan
    Write-Host "       dotnet `"$CliDll`" scan --strategy momentum --top 10" -ForegroundColor Cyan
    Write-Host ''
    exit 0
}
catch {
    Write-Host ''
    Write-Fail "Setup did not finish: $($_.Exception.Message)"
    Write-Note 'Read the red/yellow lines above for the reason.'
    Write-Note 'If you are stuck, send a screenshot of this window for help.'
    exit 1
}
