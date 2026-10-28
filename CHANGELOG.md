# Changelog

## 1.0.1 — 2026-10-28

### Fixed

- Importing, verifying, resuming and launching a game can now finish guarded shutdown when Windows retains inactive executable mappings after exit.
- Unmount checks continue to protect ordinary readers, Steam writers and memory-mapped files in both the calling process and other processes.
- Automatic shutdown/restart readiness shares the same file-use checks and continues to wait for pending uploads or unknown drive status.
- Native filesystem sync now persists local data and sparse-range metadata while a file is open. Two flushed writes survive fixture worker termination, pause, repair and restore with exact bytes.
- Completed game launches release their process handle; nonessential diagnostics permission failures allow normal completion cleanup.

### Release contents

- Windows x64 application ZIP with the Desktop, CLI, Agent and native engine `v1.75.2-cloudssd.2`.
- Source ZIP from the same Git tag, including application projects, tests, native patches, build/install helpers, assets and documentation.
- SHA-256 checksums, updated installation guides, version metadata and a reusable release-publishing helper.

### Verification and limits

The fix verification passed 155 unit tests, 79 WPF desktop checks and 25 final affected integration cases, along with the Release build, production publishing and four native runtime packages with vet disabled. Release preparation repeats the build/unit suite after changing version metadata. See [test evidence](TEST_REPORT.md).

Live cloud accounts, commercial games, general browser/application installation, Hybrid privileges, clean-PC installation, reboot/logon and sleep/resume remain unverified. No Android, macOS or Linux application package or setup EXE is supplied. New product license terms remain pending; required source/vendor notices are retained.

### Update

Finish uploads, close applications using the drive and close CloudSSD safely. Extract the complete new Windows ZIP and run `Install.cmd`, or run `CloudSSD.exe` directly from the extracted folder. Retain your existing user profile and cache; see [installation and updates](README.md#close-update-and-uninstall).
