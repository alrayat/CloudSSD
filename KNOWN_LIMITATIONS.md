# CloudSSD known limitations

Current scope: experimental Windows x64 cloud-backed storage and game preparation. See [test evidence](TEST_REPORT.md) for the exact verified boundaries.

## Storage and network

- Cold reads are limited by the internet connection, provider throughput and latency. 70 Mbps is at most 8.75 MB/s before overhead; a cloud drive's capacity is not extra physical SSD capacity.
- Prediction moves likely reads ahead of launch; unseen data still downloads on demand. A cache smaller than the game's working set can evict prepared data before use.
- Sparse files have different logical and physical sizes. Cache budgets are eviction targets: open/dirty files, upload staging, recovery snapshots and local Hybrid copies can require additional disk space.
- Pinning is best effort. The app does not bypass quotas, account limits, DRM, ownership checks or anti-cheat.
- Cached ranges can remain readable during an outage on an already mounted drive. Uncached reads fail after retries. A fresh mount still needs the provider reachable; the cache is not a guaranteed offline filesystem.

## Native filesystem and safe shutdown

CloudSSD packages **modified rclone v1.75.2**, not stock 1.75.2. It supports deferred hydration, durable metadata, recovery snapshots and external open-file telemetry. See [native design](docs/NATIVE_RCLONE.md).

- Planned unmount checks the driver's `openFiles` count and pending uploads. A file held open by another app can block unmount even without recent reads. Close games, Explorer previews and other readers before retrying; do not force-kill a drive to dismiss the warning.
- The CloudSSD TeraBox fixture import test still failed at teardown with **four reported open files** after its byte checks and demo launch succeeded. The guard remains active. This is an unresolved integration issue, not a passing test.
- The shared filesystem fixture also retained open-file telemetry after its assertions passed. Disposable test mounts required explicit cleanup after their owners exited and pending uploads were zero; successful assertions do not establish every shutdown path.
- Write-back and uploads can outlive the window. An interrupted upload retains dirty local cache data; do not delete that cache. TeraBox changes generally re-upload a whole file/part and need local staging space.
- Range metadata persists after handles close; abrupt termination can require revalidation or repeat downloads. Recovery checkpoints are evidence and are not automatically applied over Steam.
- rclone VFS does not provide all NTFS features: alternate data streams, hard links, native ACLs/EFS and reliable cross-process byte-range locking are limited or unavailable.
- Remote folder changes need refresh/polling. CloudSSD uses recursive `vfs/refresh` to re-read already listed folders.

## Permanent drives

- The background agent starts after Windows sign-in, using current-user DPAPI credentials and the user's drive-letter session. It is not a pre-login system service.
- Fixed permanent-drive letters wait when occupied; they do not replace another drive. User-disabled Windows startup entries stay disabled.
- Elevated programs may not see drive letters from the normal user session. CloudSSD does not alter Windows connection policies.
- Agent handoff/startup registration have local test coverage. Actual reboot/logon, sign-out upload drain and sleep/resume were not revalidated in the CloudSSD application session.

## Applications and games

- No commercial-game compatibility has been established by the CloudSSD session. The DRM-free demo and portable Windows tools are test workloads.
- A general app installer/launcher is not implemented. Portable apps may run, but Chrome/Edge, browser profiles, service-dependent apps, registry installers and updaters have not been verified on a cloud drive. Install these normally on local storage until tested individually.
- Steam installation directly onto cloud storage has not completed acceptance. Native fixes do not establish that every Steam installation or game works. Install locally and import appropriate games, or use Full Local through the official launcher.
- Hybrid mode requires Windows Developer Mode or appropriate symbolic-link privileges. It was not enabled here. Copied files stay local; writes through symbolic links target the cloud drive.
- A compatibility scan is a diagnostic, not proof of a successful launch. The app's Verified label requires a monitored run of at least 60 seconds without a failure signature.
- Game-Aware Mode has no verified real game plugin. Its protocol and learning tests use synthetic telemetry.

## Providers and credentials

- Google OAuth needs an enabled Drive API and appropriate consent/test-user configuration. Testing-mode refresh tokens can expire after seven days. rclone's shared client may be throttled/retired; use a personal client when needed.
- TeraBox's preferred official API needs developer credentials issued by TeraBox. The optional website session is unofficial, may trigger restrictions/verification, and can require renewed sign-in. It does not solve captchas or remove rate/plan limits.
- Current TeraBox drive/import checks used local fixtures; live-account throughput and acceptance remain unverified.
- TeraBox chunking is implemented (default 1 GiB parts), but real-account large-file acceptance remains unverified. Every part counts against quota. The native unchunked bridge still enforces the provider's file-size limit. See [large-file storage](LARGE_FILE_VIRTUALIZATION.md).
- Fresh website connections default to `/CloudSSD`; existing configured roots are respected. Changing an official app identity or existing root may expose a different folder; do not rename/move cloud data for branding.
- OneDrive, Dropbox, S3 and NAS are available through rclone configuration but unverified as CloudSSD workflows.

## Telemetry and packaging

- VFS read logs do not identify the issuing process. Prefetch attribution uses range/time windows and can overlap a foreground read. Profiling consumes some CPU/log I/O and can be disabled, which also removes dependent metrics.
- Upload throughput may be unavailable separately from combined native transfer statistics. The UI must not invent missing numbers.
- A production build and portable package were verified locally. Inno Setup is absent, so no CloudSSD setup EXE was built or install-tested. Clean-PC prerequisites, upgrade/uninstall and SmartScreen behavior remain unverified for this release.
- Native runtime regressions passed with vet disabled. Go vet was not verified in this source check; the default native-build script retains it.
