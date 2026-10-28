# CloudSSD build status

The source selected for commit builds in Release with **0 warnings and 0 errors**. **155 unit tests** and **79 real WPF desktop checks** pass. Desktop, CLI and Agent self-contained win-x64 ReadyToRun publishing, the dependency smoke check and local **1.0.1 portable ZIP** packaging pass.

The import/verify/resume/launch/guarded-shutdown test passes. New reader and mapped-file regressions protect active users, and automatic power actions share the safe readiness check. A broad integration run passed 57 of 58 cases and exposed a native sync defect; the strengthened two-write crash-recovery test fails before the fix and passes afterward. The final affected integration run passes **25 cases with no failures or skips**. See [TEST_REPORT.md](TEST_REPORT.md).

Native engine identity is **v1.75.2-cloudssd.2**. Four native runtime packages pass with vet disabled; Go vet remains unverified. The existing GitHub **1.0.0 ZIP** still contains revision `.1` and has not been replaced by these source fixes.

Live cloud accounts, commercial games, browser/general application installation, Hybrid privileges, clean-PC installation, Windows reboot/logon and sleep/resume require separate acceptance. Inno Setup is absent; no setup EXE was built or install-tested. See the test report for exact coverage and remaining limits.
