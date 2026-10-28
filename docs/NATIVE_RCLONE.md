# CloudSSD native drive engine

CloudSSD uses a source-built modified rclone v1.75.2 with WinFsp. The release script checks the required native build identity and pinned SHA-256. Upstream source: [rclone v1.75.2](https://github.com/rclone/rclone/tree/v1.75.2), MIT licensed.

## Changes and safety

Deferred hydration moves missing-range completion for partly cached dirty files into the bounded write-back queue. Close saves sparse metadata promptly. Reopen cancels hydration before reuse; failed completion remains dirty/retryable. Complete data is verified before upload.

Durable metadata, external open-file telemetry and hashed sparse recovery snapshots protect staging writes. Planned removal is refused while readers retain files. Dirty cleanup preserves valid local ranges before deletion, subject to reserve and snapshot checks. Missing ranges remain missing. Recovery data uses local disk and is not automatically applied over Steam or silently deleted.

Revision `.2` adds `diskCache.openFilePaths` alongside `openFiles`. Reading this list holds only the cache mutex and uses atomic open counts, so a busy item's lock does not block status requests. CloudSSD checks the reported paths with Windows Restart Manager, exclusive sharing probes and a calling-process mapped-view scan before accepting inactive executable mappings. Active users and incomplete telemetry cannot use that exception.

The Windows native `Fsync` callback now calls the VFS handle's `Sync` implementation instead of returning success without syncing. In full-cache mode this commits local data and its sparse-range metadata while the file is still open. It does not wait for a cloud upload or hydrate missing ranges. The crash-recovery regression writes and flushes two separate ranges, kills only its fixture worker, pauses/restores the sparse write and verifies every byte against the original plus both changes.

These mechanisms do not guarantee complete launcher installation or make cold reads independent of the provider.

## Rebuild

The existing source archive is pinned to SHA-256:

```text
68afd7f68c84bd978966feae2116339aa7bf454b39c573910c462e87c3774d5a
```

The source/patch files are under `native/rclone`. Use the installed Go 1.26+ toolchain and PowerShell 7.4+:

```powershell
& '.\installer\build-native.ps1' -GoExe go -BuildDirectory '.\.work\native-build' -Install
```

Run from the repository root and choose a fresh extraction target. If Go is not on PATH, pass the full path of your installed `go.exe`. The helper downloads only the pinned public upstream archive when it is missing, verifies its checksum, applies the patch, copies supplemental source, tests four native packages and builds a candidate. `-Install` copies a matching candidate into the ignored local tools folder; omit it to build without installing.

The recorded binary was built with Go **1.27.3**, `CGO_ENABLED=0`, `-tags cmount`, `-trimpath` and `-buildvcs=false`; its identity is `v1.75.2-cloudssd.2`. A different compiler can produce a different hash. Review the candidate and run isolated real-mount tests before using `-Install -UpdateChecksum` to establish a new pin. Preserve upstream `COPYING`. Release checks enforce both the identity and checksum.

Native runtime tests passed with `-SkipVet` in this environment. Vet was not verified; the normal command retains it. Use `-SkipVet` only when explicitly documenting that limitation.

## Verification

See [TEST_REPORT.md](../TEST_REPORT.md) for checks against this exact source. Release packaging requires the installed native candidate to match the recorded CloudSSD driver checksum.
