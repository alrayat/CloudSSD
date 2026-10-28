# CloudSSD permanent drives

Enable **Keep Cloud SSD Permanent** on the Cloud Drives page to let the background agent manage the drive after the window closes and register current-user Windows sign-in startup.

## Lifecycle

The desktop hands running mounts to `CloudSSD.Agent.exe`; reopening asks the agent to release ownership so the app can adopt them. The agent supervisor restarts its worker with bounded backoff. The worker hosts AppHost, coordinates the data-folder lock and responds on a current-user-only control pipe.

Permanent drives keep their saved letter. If another device holds it, the agent waits and reports the conflict instead of replacing the drive. It waits for provider/network availability and honors disabled Windows startup entries. Startup occurs after sign-in because DPAPI credentials and drive letters belong to the user's session.

Turning permanence off removes startup registration once no permanent drives remain. Safe unmount waits for open handles, pending uploads and recent activity to settle. Cloud files, logins and cached dirty uploads are preserved.

## Control and boundaries

Use the CLI's `agent status`, `agent release` and `agent stop` commands to inspect, hand over or request graceful shutdown. Close readers and wait for uploads before stopping; do not force-kill the native driver to dismiss a warning.

Local registration/agent/handoff checks have passed. Actual Windows reboot/logon, sign-out upload drain, sleep/resume and visibility from elevated programs remain unverified in the current acceptance run. Current source and native revision `.2` pass fixture import teardown and share safe readiness checks with automatic power actions; the published 1.0.0 ZIP remains unchanged. See [TEST_REPORT.md](TEST_REPORT.md) and [KNOWN_LIMITATIONS.md](KNOWN_LIMITATIONS.md).
