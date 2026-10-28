# Publish a CloudSSD release

Release source, compiled binaries and documentation must describe the same Git commit. Preserve existing releases and tags; publish each version with new asset names. Keep credentials, profiles, generated test data and tools out of source commits. Use installed prerequisites and retain required source/vendor notices.

## Prepare and verify

Update `Directory.Build.props`, the default version in `installer/build-release.ps1` and `installer/CloudSSD.iss`, installation/download links, CHANGELOG.md and the actual test report. Build in an isolated clean checkout when unrelated local changes exist. Verify the native identity/checksum, solution, unit tests, desktop checks and relevant real-mount cases. State any skipped or unverified checks.

Commit the reviewed source, then build the Windows package from that committed tree:

```powershell
python .\scripts\check-source.py
& .\scripts\cloudssd.ps1 -Action Build
& .\scripts\cloudssd.ps1 -Action Test
& .\installer\build-release.ps1 -Version 1.0.1
```

An already completed, matching unit run permits `-SkipTests` during packaging. The source helper retains Go vet by default; disclose `-SkipVet` when used. Inno Setup is optional and its absence does not create a setup EXE. Do not supply publisher credentials in public packages.

## Source archive and checksums

Use a fresh output folder. Export the release commit rather than the working directory so local uncommitted changes and ignored files cannot leak:

```powershell
$assets = Join-Path $PWD 'dist\release-1.0.1'
New-Item -ItemType Directory -Path $assets -ErrorAction Stop
Copy-Item .\dist\CloudSSD-1.0.1-win-x64.zip $assets
git archive --format=zip --prefix=CloudSSD-1.0.1/ --output="$assets\CloudSSD-1.0.1-source.zip" HEAD
$lines = foreach ($name in @('CloudSSD-1.0.1-win-x64.zip', 'CloudSSD-1.0.1-source.zip')) {
    $digest = (Get-FileHash (Join-Path $assets $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    $line = "$digest  $name"
    Set-Content (Join-Path $assets ($name + '.sha256')) $line
    $line
}
Set-Content (Join-Path $assets 'SHA256SUMS.txt') $lines
```

Audit both ZIPs for credentials, profiles, unwanted generated files and archive integrity. Confirm the source archive's commit comment, required notices, product/version metadata and native patches. Confirm the Windows ZIP's native SHA-256, version, documentation and CLI dependency smoke check.

## Publish and verify GitHub

Push the source commit and wait for its GitHub Actions checks to succeed. Write release notes from the final changelog, with download/install instructions, real verification coverage and remaining limits. The helper retrieves existing Git authentication in memory, performs a read-only preflight by default, requires successful CI for the selected commit, refuses conflicting tags/assets and uploads all five assets to a draft before publishing a regular Latest release:

```powershell
git push origin main
python .\scripts\publish-release.py --tag v1.0.1 --asset-dir .\dist\release-1.0.1 --notes .\dist\release-1.0.1\release-notes.md
# Only after the concrete assets, source commit, CI and notes pass review:
python .\scripts\publish-release.py --tag v1.0.1 --asset-dir .\dist\release-1.0.1 --notes .\dist\release-1.0.1\release-notes.md --publish
```

Publishing requires owner authorization. The helper never deletes or replaces an existing tag, release or asset. It verifies GitHub asset digests, final tag/commit identity and Latest status. A failed upload leaves a draft for inspection or safe retry. Review public download links and keep the source tree and release notes aligned.
