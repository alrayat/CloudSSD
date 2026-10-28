<#
.SYNOPSIS
  Uninstalls CloudSSD for the current user.
.DESCRIPTION
  Stops CloudSSD and the rclone mounts it started (pending uploads are given time to finish), removes the program
  files, shortcuts and uninstall entry. Settings, game library, profiles and the cache are KEPT unless you choose to
  remove them. Cloud files are never touched. Cache folders are only deleted if they carry the CloudSSD marker.
.PARAMETER RemoveData
  Remove settings, logs, profiles and the saved (encrypted) cloud login.
.PARAMETER RemoveCache
  Remove cached game data (only folders marked as CloudSSD caches).
#>
param([switch]$RemoveData, [switch]$RemoveCache, [switch]$Quiet)
$ErrorActionPreference = "Continue"
$installDir = $PSScriptRoot
$dataRoot = if ($env:CLOUDSSD_HOME) { $env:CLOUDSSD_HOME } else { Join-Path $env:LOCALAPPDATA 'CloudSSD' }

if (-not $Quiet -and -not $PSBoundParameters.ContainsKey('RemoveData')) {
    $a = Read-Host "Also delete CloudSSD settings, game library, profiles and saved cloud login? [y/N]"
    $RemoveData = $a -match '^[yY]'
}
if (-not $Quiet -and -not $PSBoundParameters.ContainsKey('RemoveCache')) {
    $a = Read-Host "Also delete cached game data from your SSD (cloud files are not affected)? [y/N]"
    $RemoveCache = $a -match '^[yY]'
}

# 1. Stop the background agent (Keep Cloud SSD Permanent) gracefully: it unmounts its drives and gives pending uploads
#    up to 60 s. Remove its Windows sign-in entry. Then stop the app and any rclone mounts left from this installation.
$cli = Join-Path $installDir "cloudssd-cli.exe"
if (Test-Path $cli) { & $cli agent stop | Out-Host }
Remove-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "CloudSSD" -ErrorAction SilentlyContinue
$ours = { Get-Process CloudSSD -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($installDir, 'OrdinalIgnoreCase') } }
& $ours | ForEach-Object { $_.CloseMainWindow() | Out-Null; $_.WaitForExit(60000) | Out-Null }
& $ours | Stop-Process -Force
$rclones = Get-Process rclone -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($installDir, 'OrdinalIgnoreCase') }
foreach ($r in $rclones) {
    # Data waiting for upload is not lost: it stays in the cache folder (kept below unless it has no pending uploads)
    # and is uploaded the next time the drive is mounted.
    Write-Host "Stopping rclone mount (pid $($r.Id))..."
    $r | Stop-Process -Force
}
Get-Process "CloudSSD.Agent" -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($installDir.TrimEnd('\') + '\', 'OrdinalIgnoreCase') } | Stop-Process -Force
Start-Sleep 2

# 2. Cache (only marked folders).
$settingsFile = Join-Path $dataRoot "settings.json"
$cacheRoot = Join-Path $dataRoot "cache"
if (Test-Path $settingsFile) {
    try { $s = Get-Content $settingsFile -Raw | ConvertFrom-Json; if ($s.Cache.CacheRoot) { $cacheRoot = $s.Cache.CacheRoot } } catch { }
}
if ($RemoveCache -and (Test-Path $cacheRoot)) {
    Get-ChildItem $cacheRoot -Directory -Filter "m-*" | ForEach-Object {
        $dirty = Get-ChildItem (Join-Path $_.FullName "vfsMeta") -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { (Get-Content $_.FullName -Raw -ErrorAction SilentlyContinue) -match '"Dirty"\s*:\s*true' }
        if (-not (Test-Path (Join-Path $_.FullName ".cloudssd-cache"))) { Write-Host "Skipped $($_.FullName) (no CloudSSD marker)" }
        elseif ($dirty) { Write-Host "Kept $($_.FullName): $(@($dirty).Count) file(s) have not been uploaded yet. Mount the drive once to finish the upload." -ForegroundColor Yellow }
        else {
            Write-Host "Removing cache $($_.FullName)"
            $resolvedCache=[IO.Path]::GetFullPath($_.FullName)
            $cachePrefix=[IO.Path]::GetFullPath($cacheRoot).TrimEnd('\') + '\'
            if (-not $resolvedCache.StartsWith($cachePrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Cache folder is outside the configured root.' }
            Remove-Item -LiteralPath $resolvedCache -Recurse -Force
        }
    }
}

# 3. Data.
if ($RemoveData -and (Test-Path $dataRoot)) {
    foreach ($item in "settings.json", "cloudssd.db", "cloudssd.db-wal", "cloudssd.db-shm", "rclone.conf", "rclone.conf.key", "mounts.state.json", "instance.lock") {
        Remove-Item (Join-Path $dataRoot $item) -Force -ErrorAction SilentlyContinue
    }
    foreach ($relative in 'logs','diagnostics') {
        $resolvedData=[IO.Path]::GetFullPath((Join-Path $dataRoot $relative))
        if (-not $resolvedData.StartsWith($dataRoot.TrimEnd('\') + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Data folder is outside the selected profile.' }
        Remove-Item -LiteralPath $resolvedData -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# 4. Shortcuts, uninstall entry, program files.
Remove-Item (Join-Path ([Environment]::GetFolderPath("Programs")) "CloudSSD.lnk") -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath("Desktop")) "CloudSSD.lnk") -Force -ErrorAction SilentlyContinue
Remove-Item "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CloudSSD" -Recurse -Force -ErrorAction SilentlyContinue
$removedFiles = $false
if ((Test-Path -LiteralPath (Join-Path $installDir 'CloudSSD.exe')) -and (Get-Item -LiteralPath (Join-Path $installDir 'CloudSSD.exe')).VersionInfo.ProductName -eq 'CloudSSD') {
    $helper=Join-Path $env:TEMP ('cloudssd-uninstall-' + [Guid]::NewGuid().ToString('N') + '.ps1')
    Copy-Item -LiteralPath (Join-Path $installDir 'Remove-CloudSSDProgram.ps1') -Destination $helper
    Start-Process powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$helper+'"'),'-InstallDirectory',('"'+$installDir+'"'),'-ParentProcessId',$PID) -WindowStyle Hidden
    $removedFiles = $true
} else {
    Write-Host "Program files were left in $installDir (CloudSSD executable identity could not be verified)." -ForegroundColor Yellow
}
Write-Host "CloudSSD uninstalled$(if (-not $removedFiles) { ' (shortcuts and registration removed)' }).$(if (-not $RemoveData) { " Your settings and library were kept in $dataRoot." })"
