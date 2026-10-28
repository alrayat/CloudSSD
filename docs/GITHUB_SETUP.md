# Publish CloudSSD source on GitHub

This folder contains the application projects, tests, native patch sources, assets, current guides, dependency notices and source checks. It uses a fresh local Git repository on `main`. No GitHub repository or upload is created automatically.

## Source layout

| Location | Contents |
| --- | --- |
| `CloudSSD.slnx`, `Directory.Build.props`, `global.json` | Solution, product metadata and SDK selection |
| `src/CloudSSD.*` | Desktop, CLI, Agent and shared application/service projects |
| `tests/` | Unit, integration, desktop checks and generated-demo source |
| `benchmarks/` | Benchmark runner source; no inherited results |
| `native/rclone/` | Pinned upstream patch and supplemental Go source |
| `installer/`, `scripts/` | Native/release build, run/check and install/uninstall helpers |
| `docs/`, root Markdown files | Current architecture, setup, user guidance and verification |
| `licenses/`, `tools/rclone/COPYING` | Required source and dependency notices |
| `.github/` | Build/unit/source-check workflow and review/issue templates |

Local `.work/`, `bin/`, `obj/`, `dist/`, credentials and downloaded executables are excluded. The source archive contains the committed source tree, without Git internals or local output.

CloudSSD uses its own project namespaces, `cloudssd-cli.exe`, `CloudSSD.Agent.exe`, `%LOCALAPPDATA%\CloudSSD` profile, `cloudssd.db` library, protected secrets, control pipes, bridge endpoints and installer identity. Provider-configured account roots remain user-controlled. The modified native driver is identified as `v1.75.2-cloudssd.1`.

## Review before upload

Run `python scripts/check-source.py` and inspect `git status`. Generated output, test profiles, credentials, SDK caches and downloaded executables are ignored and must stay out of commits. Required source attribution remains in `licenses/Source-MIT-NOTICE.txt`; vendor terms remain in `licenses/` and THIRD_PARTY_NOTICES.md.

The root LICENSE records that new product terms have not been supplied. Choose terms for your original contributions before advertising a new product license. Included-source and vendor rights continue to apply. A source upload must retain the required notices.

## Create the remote

Create an empty GitHub repository named **CloudSSD** in your chosen account, then use its actual URL:

```powershell
git remote add origin https://github.com/YOUR-ACCOUNT/CloudSSD.git
git push -u origin main
```

These are instructions for the owner; no push has been executed by preparing this source folder. GitHub Actions checks source safety, the Windows solution build and unit tests when triggered. Native/WinFsp, desktop and live-provider acceptance have separate requirements in [TEST_REPORT.md](../TEST_REPORT.md).

Upload compiled release archives to GitHub Releases only after review. Keep them out of the source commit. The source distribution requires the native build step for a working virtual drive; see [native setup](NATIVE_RCLONE.md).
