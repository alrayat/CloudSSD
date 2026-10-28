# CloudSSD security

CloudSSD handles Google OAuth tokens, optional TeraBox credentials/browser sessions and user files. Share vulnerability details privately with the repository owner. If this repository has GitHub private vulnerability reporting enabled, use **Security → Report a vulnerability**. No separate support address or reporting service is configured by this documentation.

## Credentials and local interfaces

- Google sign-in uses Google's page in your browser via rclone; CloudSSD does not request the Google password. Official TeraBox login uses its approved OAuth flow. The optional website flow stores a session from the TeraBox page and is clearly marked unofficial.
- The default rclone configuration is encrypted; its random key is protected with Windows DPAPI for the current user. TeraBox token/client/session/bridge-secret files also use DPAPI. Turning config encryption off changes that protection.
- rclone remote control and the TeraBox bridge listen on `127.0.0.1` with random credentials. The agent and game telemetry use current-user-only named pipes. Loopback access is not a substitute for protecting the Windows account.
- Logs and exports use redaction. Inspect a sanitized report before sharing it; filenames, paths and operational context can still be sensitive. The private rclone config is excluded from diagnostic exports.

Use **Settings → Open data folder** to find the active profile. `CLOUDSSD_HOME` can select another profile for portable/test work. Copying DPAPI files to another Windows account may not make them usable there.

## Files and dependencies

Cache cleanup is guarded by the configured root and cache markers, and must preserve dirty uploads and cloud originals. Planned unmount checks open handles and uploads. Do not weaken that guard or force-stop a mounted drive to conceal a test failure. Recovery snapshots are retained evidence, not an instruction to overwrite Steam files.

Native release packaging checks the modified rclone version/hash. Optional prerequisite installers are checked for expected Authenticode signatures. CloudSSD itself is not code-signed. WinFsp and WebView2 retain their own licenses and update behavior.

Never commit or publicly attach OAuth client secrets, tokens, cookies, `rclone.conf`, key files, WebView2 profiles, raw logs, or diagnostic archives without inspection. Publisher JSON files under `installer/` are ignored; release packaging excludes them by default and includes them only with explicit `-IncludePublisherCredentials`. A release ZIP must be inspected before publication.

## A useful private report

Include the affected CloudSSD/native version, a minimal reproduction, the observed impact and redacted logs. Explain whether the issue involves a live provider or a local fixture. Send secrets only through an agreed private channel if the owner determines they are necessary; usually a reproduction should use disposable credentials.

CloudSSD does not silently alter Windows security settings or bypass provider restrictions, game ownership or anti-cheat. Provider sessions, account restrictions and general application compatibility have separate limits in [KNOWN_LIMITATIONS.md](KNOWN_LIMITATIONS.md).
