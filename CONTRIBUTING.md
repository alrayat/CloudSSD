# Contributing to CloudSSD

Use the existing WPF/.NET 10 architecture and real services. Preserve user data, API contracts, filesystem operations and unrelated uncommitted changes.

## Development

Inspect repository instructions and Git status before editing. Create a checkpoint/backup before substantial changes. Use installed tools; .NET 10 SDK and PowerShell are needed for the app, WinFsp for real mounts, Go 1.26+ for native rebuilds and Inno Setup 6 only for optional setup EXE packaging.

From the source checkout:

```powershell
& '.\scripts\cloudssd.ps1' -Action Build
& '.\scripts\cloudssd.ps1' -Action Test
& '.\scripts\cloudssd.ps1' -Action Run
& '.\scripts\cloudssd.ps1' -Action DesktopChecks
```

The helper locates the actual solution/projects and selects an existing .NET 10 SDK. Set `DOTNET_ROOT` if needed. Desktop checks open real WPF windows against an isolated profile without live sign-in.

Mount/cache/streaming/provider changes need relevant real-mount integration cases from the source test folder. Use disposable profiles/letters and required demo fixtures. Preserve dirty uploads and never stop unrelated user mounts. Report cases and failures precisely; no full integration-suite pass is currently claimed.

## Native and release

The release requires the pinned modified rclone driver. Preserve upstream licensing and run native and real-mount regressions before replacing it. See [native build instructions](docs/NATIVE_RCLONE.md).

```powershell
# PowerShell 7.4+, existing Go/archive, fresh extraction directory:
& '.\installer\build-native.ps1' -GoExe go -BuildDirectory '.\.work\native-build' -Install
# Select an unused version:
& '.\installer\build-release.ps1' -Version 1.0.0
```

The native helper produces a candidate and hash. `-Install` requires the candidate to match the recorded checksum. See the native guide before reviewing a different toolchain build or changing the pin. The default command includes Go vet; `-SkipVet` must be disclosed separately from runtime test results.

Release packaging checks the driver, runs unit tests, publishes desktop/CLI/agent self-contained binaries, includes current documentation/notices and runs CLI dependency checks. It creates a ZIP/checksum and optionally an installer EXE when its compiler exists. Existing versions are preserved. Publisher credentials require explicit `-IncludePublisherCredentials`; inspect packages before distribution and do not upload without authorization.

## Review

- Preserve actual services and state; do not substitute demo data for production features.
- Keep cache cleanup guarded, dirty uploads/saves safe and normal launcher/ownership/anti-cheat behavior intact.
- Record measured results, commands and unverified boundaries; mocks/simulations are not live-provider acceptance.
- Respect included source and vendor licensing; product licensing is documented in [LICENSE](LICENSE).
- Keep tokens, configs and private diagnostics out of commits. See [SECURITY.md](SECURITY.md).
