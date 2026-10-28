<#
.SYNOPSIS
  Builds the CloudSSD release: self-contained win-x64 publish of the desktop app and the cloudssd-cli CLI into one folder,
  bundles the checksum-verified rclone binary, documentation and install/uninstall scripts, and zips the result.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File installer\build-release.ps1
#>
param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0",
    [string]$WinFspVersion = "2.1.25156",
    [switch]$SkipTests,
    [switch]$IncludePublisherCredentials
)
$ErrorActionPreference = "Stop"
$dotnet = if ($env:DOTNET_ROOT -and (Test-Path -LiteralPath (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) { Join-Path $env:DOTNET_ROOT 'dotnet.exe' } else { 'dotnet' }
if (-not ((& $dotnet --list-sdks) -match '^10\.')) { throw '.NET 10 SDK is required. Set DOTNET_ROOT to its existing installation.' }
$repo = Resolve-Path "$PSScriptRoot\.."
$dist = Join-Path $repo "dist"
$name = "CloudSSD-$Version-win-x64"
$stage = Join-Path $dist $name

Write-Host "== CloudSSD $Version release build ==" -ForegroundColor Cyan
$resolvedStage = [IO.Path]::GetFullPath($stage)
$resolvedDist = [IO.Path]::GetFullPath($dist).TrimEnd('\') + '\'
if (-not $resolvedStage.StartsWith($resolvedDist, [StringComparison]::OrdinalIgnoreCase)) { throw "Release stage is outside dist." }
if (Test-Path -LiteralPath $resolvedStage) { throw "Release stage already exists: $resolvedStage. Preserve it and select a new Version." }
New-Item -ItemType Directory -Force $stage | Out-Null

# 1. Verify the modified driver's version and recorded binary checksum before shipping it.
$rclone = Join-Path $repo "tools\rclone\rclone.exe"
$sums = Get-Item (Join-Path $repo "tools\rclone\SHA256SUMS.cloudssd.1") -ErrorAction SilentlyContinue
if (-not (Test-Path $rclone) -or -not $sums) { throw "Native driver or SHA256SUMS.cloudssd.1 missing. See native\rclone\README.md." }
$rcloneVersion = ($sums.Name -replace '^SHA256SUMS\.', '')
$nativeVersion = (& $rclone version | Select-Object -First 1)
if ($nativeVersion -notmatch 'v1\.75\.2-cloudssd\.1') { throw "Build the deferred-hydration driver first; see native\rclone\README.md." }
$expectedNativeHash = ((Get-Content $sums.FullName | Select-Object -First 1) -split '\s+')[0]
if ((Get-FileHash $rclone -Algorithm SHA256).Hash -ne $expectedNativeHash) { throw "Native driver SHA256 mismatch." }
$nativeVersion | Write-Host

# 2. Tests (unit tests always; integration tests need WinFsp and take several minutes).
if (-not $SkipTests) {
    & $dotnet test (Join-Path $repo "tests\CloudSSD.Tests") -c $Configuration --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Unit tests failed" }
}

# 3. Publish (self-contained so users do not need to install .NET).
$common = @("-c", $Configuration, "-r", "win-x64", "--self-contained", "true", "-p:PublishReadyToRun=true", "-p:Version=$Version", "--nologo", "-v", "q", "-o", $stage)
& $dotnet publish (Join-Path $repo "src\CloudSSD.Desktop\CloudSSD.Desktop.csproj") @common
if ($LASTEXITCODE -ne 0) { throw "Desktop publish failed" }
& $dotnet publish (Join-Path $repo "src\CloudSSD.Cli\CloudSSD.Cli.csproj") @common
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed" }
& $dotnet publish (Join-Path $repo "src\CloudSSD.Agent\CloudSSD.Agent.csproj") @common
if ($LASTEXITCODE -ne 0) { throw "Agent publish failed" }

# 4. rclone + notices.
$toolDir = Join-Path $stage "tools\rclone"
New-Item -ItemType Directory -Force $toolDir | Out-Null
Copy-Item $rclone $toolDir -Force
Copy-Item $sums.FullName $toolDir -Force
$hash = (Get-FileHash (Join-Path $toolDir "rclone.exe") -Algorithm SHA256).Hash.ToLower()
Set-Content (Join-Path $toolDir "README.txt") @"
CloudSSD native drive engine: modified rclone v1.75.2, patch revision 1 (https://rclone.org).
MIT License, Copyright (C) 2012 by Nick Craig-Wood.
Built from upstream v1.75.2 with CloudSSD's deferred-hydration patch. This is a modified build.
See docs/NATIVE_RCLONE.md for source, patch, tests and build instructions. SHA256 of rclone.exe: $hash
"@
Copy-Item (Join-Path $repo "THIRD_PARTY_NOTICES.md") $stage -Force
Copy-Item (Join-Path $repo "LICENSE") $stage -Force
Copy-Item (Join-Path $repo "licenses") $stage -Recurse -Force

# Optional publisher Google OAuth client (Desktop app client JSON from Google Cloud, renamed). It is only useful to
# everyone once the Google project is "In production"; in Testing mode only listed test users can sign in with it.
if ($IncludePublisherCredentials) {
foreach ($k in "terabox-client.json") {
    $src = Join-Path $PSScriptRoot $k
    if (Test-Path $src) { Copy-Item $src (Join-Path $stage $k) -Force; Write-Host "Bundled publisher TeraBox credentials ($k)." -ForegroundColor Yellow }
}
$publisherKey = Join-Path $PSScriptRoot "google-client.json"
if (Test-Path $publisherKey) {
    Copy-Item $publisherKey (Join-Path $stage "google-client.json") -Force
    Write-Host "Bundled publisher Google key (installer\google-client.json)." -ForegroundColor Yellow
}
}

# 5. Docs and scripts.
Get-ChildItem -LiteralPath $repo -Filter '*.md' -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $stage -Force }
New-Item -ItemType Directory -Force (Join-Path $stage "docs") | Out-Null
Copy-Item (Join-Path $repo "docs\*") (Join-Path $stage "docs") -Recurse -Force
Copy-Item (Join-Path $repo "native") $stage -Recurse -Force
Copy-Item (Join-Path $repo "tools\rclone\COPYING") $toolDir -Force
Copy-Item (Join-Path $PSScriptRoot "Install-CloudSSD.ps1") $stage -Force
Copy-Item (Join-Path $PSScriptRoot "Uninstall-CloudSSD.ps1") $stage -Force
Copy-Item (Join-Path $PSScriptRoot "Remove-CloudSSDProgram.ps1") $stage -Force
Set-Content (Join-Path $stage "Install.cmd") "@echo off`r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0Install-CloudSSD.ps1`" %*`r`npause"
Remove-Item (Join-Path $stage "*.pdb") -ErrorAction SilentlyContinue

# 6. Smoke test of the published binaries (no UI): CLI must run and find rclone/WinFsp.
& (Join-Path $stage "cloudssd-cli.exe") deps
if ($LASTEXITCODE -ne 0) { throw "cloudssd-cli deps failed in the published folder" }

# 7. Zip + checksum.
$zip = Join-Path $dist "$name.zip"
if (Test-Path -LiteralPath $zip) { throw "Release archive already exists: $zip. Preserve it and select a new Version." }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal
$zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
Set-Content "$zip.sha256" "$zipHash  $name.zip"
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Release: $zip ($size MB)`nSHA256: $zipHash" -ForegroundColor Green

# 8. Single-file setup.exe (Inno Setup 6) with the official WinFsp installer inside.
$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    Write-Host "Inno Setup not found: skipped setup.exe (install it with: winget install JRSoftware.InnoSetup)." -ForegroundColor Yellow
    return
}
$winfspDir = Join-Path $repo "tools\winfsp"
$winfspMsi = Join-Path $winfspDir "winfsp-$WinFspVersion.msi"
if (-not (Test-Path $winfspMsi)) {
    New-Item -ItemType Directory -Force $winfspDir | Out-Null
    $tag = "v" + ($WinFspVersion -replace '\.\d+$', '')
    Invoke-WebRequest "https://github.com/winfsp/winfsp/releases/download/$tag/winfsp-$WinFspVersion.msi" -OutFile $winfspMsi -UseBasicParsing
}
# Ship only the genuine installer: it must carry a valid Authenticode signature from WinFsp's publisher.
$sig = Get-AuthenticodeSignature $winfspMsi
if ($sig.Status -ne "Valid" -or $sig.SignerCertificate.Subject -notmatch "NAVIMATICS LLC") {
    Remove-Item $winfspMsi -Force
    throw "WinFsp installer signature check failed ($($sig.Status)); deleted it."
}
$webViewBootstrapper = Join-Path $repo "tools\webview2\MicrosoftEdgeWebview2Setup.exe"
if (-not (Test-Path -LiteralPath $webViewBootstrapper)) {
    New-Item -ItemType Directory -Force (Split-Path $webViewBootstrapper) | Out-Null
    Invoke-WebRequest "https://go.microsoft.com/fwlink/p/?LinkId=2124703" -OutFile $webViewBootstrapper -UseBasicParsing
}
$webViewSignature = Get-AuthenticodeSignature $webViewBootstrapper
if ($webViewSignature.Status -ne "Valid" -or $webViewSignature.SignerCertificate.Subject -notmatch "Microsoft Corporation") {
    throw "Microsoft WebView2 bootstrapper signature check failed."
}
& $iscc /Q "/DAppVersion=$Version" "/DSourceDir=$stage" "/DWinFspMsi=$winfspMsi" "/DWebView2Bootstrapper=$webViewBootstrapper" "/O$dist" (Join-Path $PSScriptRoot "CloudSSD.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
$setup = Join-Path $dist "CloudSSD-$Version-setup.exe"
$setupHash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLower()
Set-Content "$setup.sha256" "$setupHash  CloudSSD-$Version-setup.exe"
Write-Host "Installer: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)`nSHA256: $setupHash" -ForegroundColor Green
