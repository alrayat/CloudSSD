#requires -Version 7.4
# Builds the pinned CloudSSD drive engine with an existing Go toolchain.
param(
    [string]$GoExe = 'go',
    [string]$SourceArchive = (Join-Path $PSScriptRoot '..\.work\rclone-v1.75.2.tar.gz'),
    [string]$BuildDirectory = (Join-Path $PSScriptRoot '..\.work\native-build'),
    [switch]$SkipVet,
    [switch]$Install,
    [switch]$UpdateChecksum
)
$ErrorActionPreference = 'Stop'
$GoExe = (Get-Command $GoExe -ErrorAction Stop).Source
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$build = [IO.Path]::GetFullPath($BuildDirectory)
$sourceArchivePath = [IO.Path]::GetFullPath($SourceArchive)
if (-not $build.StartsWith($repo + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Native build must stay inside the repository.' }
if (Test-Path -LiteralPath $build) { throw 'Build directory exists. Choose a fresh BuildDirectory.' }
if (-not (Test-Path -LiteralPath $sourceArchivePath)) {
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($sourceArchivePath)) -Force | Out-Null
    Invoke-WebRequest 'https://codeload.github.com/rclone/rclone/tar.gz/refs/tags/v1.75.2' -OutFile $sourceArchivePath
}
if ((Get-FileHash -LiteralPath $sourceArchivePath -Algorithm SHA256).Hash -ne '68afd7f68c84bd978966feae2116339aa7bf454b39c573910c462e87c3774d5a') { throw 'Pinned source archive checksum mismatch.' }
New-Item -ItemType Directory -Path $build | Out-Null
$archive = [IO.File]::OpenRead($sourceArchivePath)
$gzip = [IO.Compression.GZipStream]::new($archive, [IO.Compression.CompressionMode]::Decompress)
$reader = [System.Formats.Tar.TarReader]::new($gzip)
try {
    while ($entry = $reader.GetNextEntry()) {
        if ([string]::IsNullOrWhiteSpace($entry.Name)) { continue }
        $target = [IO.Path]::GetFullPath((Join-Path $build $entry.Name.Replace('/', '\')))
        if (-not $target.StartsWith($build + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive path outside build directory.' }
        if ($entry.EntryType -eq [System.Formats.Tar.TarEntryType]::Directory) { [IO.Directory]::CreateDirectory($target) | Out-Null }
        elseif ($entry.EntryType -in @([System.Formats.Tar.TarEntryType]::RegularFile, [System.Formats.Tar.TarEntryType]::V7RegularFile)) {
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            $stream = [IO.File]::Create($target)
            try { if ($entry.DataStream) { $entry.DataStream.CopyTo($stream) } } finally { $stream.Dispose() }
        }
    }
} finally { $reader.Dispose(); $gzip.Dispose(); $archive.Dispose() }
$source = Join-Path $build 'rclone-1.75.2'
$savedCgo = $env:CGO_ENABLED
try {
    $env:CGO_ENABLED = '0'
    $sourceRelative = [IO.Path]::GetRelativePath($repo, $source).Replace('\', '/')
    & git -C $repo apply --check "--directory=$sourceRelative" (Join-Path $repo 'native\rclone\deferred-hydration.patch')
    if ($LASTEXITCODE -ne 0) { throw 'Native patch does not apply cleanly.' }
    & git -C $repo apply "--directory=$sourceRelative" (Join-Path $repo 'native\rclone\deferred-hydration.patch')
    if ($LASTEXITCODE -ne 0) { throw 'Native patch failed.' }
    Get-ChildItem -LiteralPath (Join-Path $repo 'native\rclone') -Filter '*.go' -File | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $source 'vfs\vfscache')
    }
    Push-Location $source
    try {
        $testArgs = @('test')
        if ($SkipVet) { $testArgs += '-vet=off'; Write-Warning 'Go vet explicitly disabled; runtime tests do not constitute vet verification.' }
        $testArgs += @('./vfs', './vfs/vfscache', './vfs/vfscache/writeback', './vfs/vfscache/downloaders', '-count=1', '-timeout=180s')
        & $GoExe @testArgs
        if ($LASTEXITCODE -ne 0) { throw 'Native regression tests failed.' }
        & $GoExe build -tags cmount -trimpath -buildvcs=false -ldflags '-s -w -X github.com/rclone/rclone/fs.Version=v1.75.2-cloudssd.1' -o rclone.exe .
        if ($LASTEXITCODE -ne 0) { throw 'Native build failed.' }
    } finally { Pop-Location }
    $candidate = Join-Path $source 'rclone.exe'
    $hash = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLower()
    & $candidate version
    Write-Host "Candidate: $candidate; SHA256: $hash. Verify real-mount workflows before installing."
    if ($Install) {
        $toolDir = Join-Path $repo 'tools\rclone'
        $checksumPath = Join-Path $toolDir 'SHA256SUMS.cloudssd.1'
        if (-not $UpdateChecksum) {
            if (-not (Test-Path -LiteralPath $checksumPath)) { throw 'No pinned binary checksum. Review the candidate and explicitly select UpdateChecksum for the first pin.' }
            $expectedHash = ((Get-Content -LiteralPath $checksumPath | Select-Object -First 1) -split '\s+')[0]
            if ($hash -ne $expectedHash) { throw 'Candidate differs from pinned binary. Review toolchain/source changes before updating the checksum.' }
        }
        New-Item -ItemType Directory -Path $toolDir -Force | Out-Null
        Copy-Item -LiteralPath $candidate -Destination (Join-Path $toolDir 'rclone.exe') -Force
        if ($UpdateChecksum) { Set-Content -LiteralPath $checksumPath -Value "$hash  rclone.exe" }
        Copy-Item -LiteralPath (Join-Path $source 'COPYING') -Destination $toolDir -Force
        Write-Host 'Installed the native candidate into the local tools folder.'
    }
} finally { $env:CGO_ENABLED = $savedCgo }
