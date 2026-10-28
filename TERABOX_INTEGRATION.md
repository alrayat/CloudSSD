# CloudSSD TeraBox integration

CloudSSD retains two real TeraBox transports behind a local WebDAV bridge. The **official Open Platform** is preferred and needs approved developer credentials. The optional website session is unofficial, carries an explicit in-app warning/consent step, and may trigger provider restrictions or renewed verification. No real TeraBox account was connected during the the current source check CloudSSD acceptance session.

## Connect

Open **Cloud Drives → Connect TeraBox**.

| Method | Setup | Scope |
| --- | --- | --- |
| Official API | Load `terabox-client.json` with TeraBox-issued `client_id`, `client_secret`, `private_secret` and configured `app_name`; approve the QR/device-code flow in the TeraBox app | Official app folder `/From: Other Applications/<App>/` |
| Website session (unofficial) | Read the warning, explicitly accept it if appropriate, and sign in on the TeraBox page inside the WebView2 dialog | Configured `Settings.TeraBox.WebRoot`; fresh default `/CloudSSD`; configured roots are respected |

Google/TeraBox publisher credentials are **excluded from releases by default**. Load your approved credentials in the app. `installer/build-release.ps1 -IncludePublisherCredentials` is an explicit opt-in packaging action, not an automatic copy. Never commit client secrets, browser sessions or key files. The optional website flow requires the separately installed Microsoft WebView2 Runtime; the NuGet SDK alone is not the browser runtime.

CloudSSD stores tokens, client information and the captured website session under the active data folder using current-user DPAPI. Use **Settings → Open data folder** for the actual profile path. Do not change an existing official app name or move its root merely for branding; that can select a different provider folder.

## Architecture and real transfers

```text
Explorer / game
  -> T: via WinFsp
  -> modified rclone VFS cache (terabox: or terabox-chunked:)
  -> authenticated WebDAV on 127.0.0.1
  -> cloudssd-cli terabox-bridge
  -> HTTPS official API or configured website-session transport
  -> TeraBox
```

The TeraBox service starts/adopts/stops the bridge and configures the remotes. Authentication, API clients and bridge live in the provider layer. Local fixtures are used only for tests; production requests use the configured real provider.

The bridge translates listing, quota, ranged reads, uploads, folder creation and move/copy/delete. It validates upstream response ranges/lengths/object size before returning bytes, retries throttling/server failures, and resumes truncated/stalled downloads from the exact offset. Upload shards are checked by MD5; whole-file verification is a separate importer choice. The website's returned `md5` must not be assumed to be the original file checksum.

## Limits and large files

- Provider rate, storage quota, session and object-size restrictions still apply. The connection does not bypass verification, captchas, plan or ownership rules.
- The configured rclone chunker overlay stores large logical files as parts (default 1 GiB) and metadata. Explorer sees one logical file; each part uses quota. The native unchunked bridge still rejects oversized objects. See [large-file storage](LARGE_FILE_VIRTUALIZATION.md).
- Empty-file support differs by transport; the website path refuses unsupported 0-byte uploads rather than inventing success.
- Writes require local staging; changing a file can re-upload a whole file/part. Cloud quota is not extra local SSD capacity. TeraBox write-back can have a longer provider-specific settling delay.
- A provider session can expire. Reconnect when requested. Current real-account chunked large-file acceptance is unverified.
- Cold asset reads remain provider-bound. Current live-provider throughput has not been measured.

## Verify your own connection

After connecting, `cloudssd-cli terabox-probe` can exercise actual account capabilities and write a redacted report in the active diagnostics directory. The probe **creates/uploads/overwrites/deletes synthetic files in its own temporary `.cloudssd-cli-probe-*` folder**; it is not a read-only health check. Review options and run it only on an account where you authorize those test operations. It does not establish commercial-game compatibility.

Local test fixtures can exercise the transport without provider credentials:

```powershell
# From a release folder containing cloudssd-cli.exe; choose a disposable local folder.
.\cloudssd-cli.exe terabox-mock --data C:\temp\cloudssd-terabox-test
```

Load the fixture credentials printed by that command in **Connect TeraBox** only for local testing. Production sign-in continues to use real provider APIs.

## Verification

See [TEST_REPORT.md](TEST_REPORT.md) for local-fixture and live-provider acceptance boundaries. Real requests remain in the production provider; tests do not replace them with fixtures.
