<#
.SYNOPSIS
  Installs CloudSSD for the current Windows user (no administrator rights needed).
.DESCRIPTION
  Copies the application to %LOCALAPPDATA%\Programs\CloudSSD, creates a Start menu shortcut (and optionally a
  desktop shortcut), and registers an uninstall entry under "Installed apps". WinFsp is required for the virtual
  drive; if it is missing this script tells you where to get it but does not install it silently.
.PARAMETER DesktopShortcut
  Also create a desktop shortcut.
.PARAMETER InstallDir
  Custom installation folder.
#>
param(
    [switch]$DesktopShortcut,
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\CloudSSD"),
    [switch]$Quiet
)
$ErrorActionPreference = "Stop"
$src = $PSScriptRoot
if (-not (Test-Path (Join-Path $src "CloudSSD.exe"))) { throw "Run this script from the extracted CloudSSD release folder." }

$running = Get-Process CloudSSD -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, 'OrdinalIgnoreCase') }
if ($running) { throw "CloudSSD is running from $InstallDir. Close it first." }

Write-Host "Installing CloudSSD to $InstallDir ..."
New-Item -ItemType Directory -Force $InstallDir | Out-Null
# Replace program files only; user data lives in %LOCALAPPDATA%\CloudSSD and is never touched here.
Get-ChildItem $src -Force | Where-Object { $_.Name -notin @("Install.cmd") } | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $InstallDir $_.Name) -Recurse -Force
}

$exe = Join-Path $InstallDir "CloudSSD.exe"
$shell = New-Object -ComObject WScript.Shell
function New-Shortcut($path) {
    $lnk = $shell.CreateShortcut($path)
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $InstallDir
    $lnk.IconLocation = "$exe,0"
    $lnk.Description = "CloudSSD - Cloud-to-SSD game storage engine"
    $lnk.Save()
}
$startMenu = Join-Path ([Environment]::GetFolderPath("Programs")) "CloudSSD.lnk"
New-Shortcut $startMenu
if ($DesktopShortcut) { New-Shortcut (Join-Path ([Environment]::GetFolderPath("Desktop")) "CloudSSD.lnk") }

# Uninstall entry (per user).
$key = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CloudSSD"
New-Item -Path $key -Force | Out-Null
$version = (Get-Item $exe).VersionInfo.ProductVersion
Set-ItemProperty $key DisplayName "CloudSSD"
Set-ItemProperty $key DisplayVersion $version
Set-ItemProperty $key Publisher "CloudSSD"
Set-ItemProperty $key InstallLocation $InstallDir
Set-ItemProperty $key DisplayIcon "$exe,0"
Set-ItemProperty $key NoModify 1 -Type DWord
Set-ItemProperty $key NoRepair 1 -Type DWord
Set-ItemProperty $key UninstallString "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$InstallDir\Uninstall-CloudSSD.ps1`""
$size = [int]((Get-ChildItem $InstallDir -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
Set-ItemProperty $key EstimatedSize $size -Type DWord

# Prerequisite check (informational only).
$winfsp = Test-Path "${env:ProgramFiles(x86)}\WinFsp\bin\winfsp-x64.dll"
Write-Host ""
Write-Host "CloudSSD $version installed." -ForegroundColor Green
if (-not $winfsp) {
    Write-Host "WinFsp is NOT installed. The virtual drive needs it." -ForegroundColor Yellow
    Write-Host "  Download the official installer from https://winfsp.dev/rel/  (or run: winget install WinFsp.WinFsp)"
} else { Write-Host "WinFsp: found." }
Write-Host "Start CloudSSD from the Start menu. Google Drive setup: docs\GOOGLE_DRIVE_SETUP.md"
if (-not $Quiet) { Start-Process $exe }
