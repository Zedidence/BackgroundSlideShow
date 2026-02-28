#Requires -Version 5.1
<#
.SYNOPSIS
    First-time setup script for BackgroundSlideShow.
.DESCRIPTION
    Installs the .NET 8 SDK (if missing) and restores all NuGet packages.
    Run once from the repo root before building.
.NOTES
    Run with: powershell -ExecutionPolicy Bypass -File docs\setup.ps1
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step([string]$msg) {
    Write-Host "`n==> $msg" -ForegroundColor Cyan
}

function Write-OK([string]$msg) {
    Write-Host "    [OK] $msg" -ForegroundColor Green
}

function Write-Fail([string]$msg) {
    Write-Host "    [FAIL] $msg" -ForegroundColor Red
}

# ---------------------------------------------------------------------------
# 1. Locate the project root (one level up from this script)
# ---------------------------------------------------------------------------
$scriptDir  = $PSScriptRoot
$projectDir = Split-Path -Parent $scriptDir
$csproj     = Join-Path $projectDir "BackgroundSlideShow.csproj"

if (-not (Test-Path $csproj)) {
    Write-Fail "Could not find BackgroundSlideShow.csproj in '$projectDir'."
    Write-Host "  Make sure you are running this script from inside the repo." -ForegroundColor Yellow
    exit 1
}

Write-Host "`nBackgroundSlideShow - First-Time Setup" -ForegroundColor White
Write-Host "Project root: $projectDir"

# ---------------------------------------------------------------------------
# 2. Check / install .NET 8 SDK
# ---------------------------------------------------------------------------
Write-Step "Checking for .NET 8 SDK..."

$dotnetOk = $false
try {
    $sdks = & dotnet --list-sdks 2>$null
    $dotnetOk = @($sdks | Where-Object { $_ -match "^8\." }).Count -gt 0
} catch { }

if ($dotnetOk) {
    Write-OK ".NET 8 SDK is already installed."
} else {
    Write-Host "    .NET 8 SDK not found. Attempting to install via winget..." -ForegroundColor Yellow

    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget) {
        winget install --id Microsoft.DotNet.SDK.8 --silent --accept-package-agreements --accept-source-agreements
        # Refresh PATH for the current session
        $env:PATH = ([System.Environment]::GetEnvironmentVariable("PATH", "Machine") + ";" +
                    [System.Environment]::GetEnvironmentVariable("PATH", "User"))

        $sdks = & dotnet --list-sdks 2>$null
        if (@($sdks | Where-Object { $_ -match "^8\." }).Count -gt 0) {
            Write-OK ".NET 8 SDK installed successfully."
        } else {
            Write-Fail "winget install finished but 'dotnet' still not found. Please restart your terminal and re-run this script."
            exit 1
        }
    } else {
        Write-Fail "winget is not available on this machine."
        Write-Host "" -ForegroundColor Yellow
        Write-Host "  Please install the .NET 8 SDK manually:" -ForegroundColor Yellow
        Write-Host "    https://dotnet.microsoft.com/en-us/download/dotnet/8.0" -ForegroundColor Yellow
        Write-Host "" -ForegroundColor Yellow
        Write-Host "  Then re-run this script." -ForegroundColor Yellow
        exit 1
    }
}

# ---------------------------------------------------------------------------
# 3. Restore NuGet packages
#    Packages pulled in by the project:
#      - Microsoft.EntityFrameworkCore.Sqlite  8.0.0
#      - SixLabors.ImageSharp                  3.1.12
#      - H.NotifyIcon.Wpf                      2.2.0
#      - CommunityToolkit.Mvvm                 8.3.2
# ---------------------------------------------------------------------------
Write-Step "Restoring NuGet packages..."

Push-Location $projectDir
try {
    & dotnet restore "BackgroundSlideShow.csproj" --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore exited with code $LASTEXITCODE" }
    Write-OK "All packages restored."
} finally {
    Pop-Location
}

# ---------------------------------------------------------------------------
# 4. Optional: verify the project builds
# ---------------------------------------------------------------------------
Write-Step "Verifying build (dotnet build)..."

Push-Location $projectDir
try {
    & dotnet build "BackgroundSlideShow.csproj" --no-restore --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet build exited with code $LASTEXITCODE" }
    Write-OK "Build succeeded."
} finally {
    Pop-Location
}

# ---------------------------------------------------------------------------
# Done
# ---------------------------------------------------------------------------
Write-Host "`n==> Setup complete! You can now run the app with:" -ForegroundColor Cyan
Write-Host "      dotnet run --project BackgroundSlideShow.csproj" -ForegroundColor White
Write-Host "    or open BackgroundSlideShow.csproj in Visual Studio / Rider.`n"
