# CloudSSD build status

CloudSSD is an experimental WPF/.NET 10 Windows x64 application. The Release solution build passed with **0 warnings and 0 errors**. Self-contained Desktop, CLI and Agent publishing, the packaged dependency check and the **1.0.0 portable ZIP** succeeded.

Verification passed **153 unit tests**, **79 desktop checks**, **24 selected integration tests** and four native runtime packages. **One integration test failed** during import shutdown; Go vet and setup EXE installation were not verified. See [TEST_REPORT.md](TEST_REPORT.md) for evidence and exact boundaries.

Live provider authentication, commercial gameplay, browser/general application installation, Windows reboot/logon, sleep/resume and installer installation require separate acceptance. No universal support claim is made.
