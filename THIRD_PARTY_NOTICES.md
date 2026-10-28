# CloudSSD third-party notices

CloudSSD bundles or depends on the following components.

## rclone (bundled: `tools/rclone/rclone.exe`, modified v1.75.2)

https://rclone.org — MIT License

```
Copyright (C) 2012 by Nick Craig-Wood http://www.craig-wood.com/nick/

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```

CloudSSD uses a source-built modified rclone, not the stock download. Upstream v1.75.2 source is fetched from `https://codeload.github.com/rclone/rclone/tar.gz/refs/tags/v1.75.2` and verified against the recorded archive SHA-256. The patch/additional source are in `native/rclone`; release packaging verifies the pinned native binary checksum. Upstream MIT `COPYING` is retained beside the binary. See [native source and build provenance](docs/NATIVE_RCLONE.md).

## WinFsp (external runtime; optional installer v2.1.25156)

https://winfsp.dev, Copyright (C) Bill Zissimopoulos / Navimatics LLC. GPLv3 with a FLOSS exception. Source
code: https://github.com/winfsp/winfsp. When the optional CloudSSD setup EXE is built, it carries the unmodified, Authenticode-signed
`winfsp-2.1.25156.msi` from https://github.com/winfsp/winfsp/releases and runs it only when WinFsp is missing.
CloudSSD uses WinFsp through rclone; rclone uses the WinFsp installation present on the system. WinFsp is a separate
program with its own uninstall entry ("WinFsp 2025").

## NuGet dependencies

The following versions come from the existing project manifests and restored package metadata. SDK and runtime licenses are separate; no Microsoft or provider endorsement is implied.

| Package/component | Version | Terms |
| --- | --- | --- |
| CommunityToolkit.Mvvm | 8.4.2 | MIT; .NET Foundation and Contributors |
| Microsoft.Data.Sqlite | 10.0.12 | MIT; Microsoft |
| SQLitePCLRaw core/bundle/provider/native packaging | 2.1.12 (restored dependency) | Apache-2.0; Eric Sink and contributors; see [license](licenses/Apache-2.0.txt) |
| SQLite engine | bundled by SQLitePCLRaw | Public domain; [SQLite copyright](https://sqlite.org/copyright.html) |
| Microsoft.Extensions.Logging / Logging.Abstractions / DependencyInjection | 10.0.12 | MIT; .NET Foundation and Contributors |
| System.Security.Cryptography.ProtectedData | 10.0.12 | MIT; .NET Foundation and Contributors |
| System.Diagnostics.EventLog | 10.0.12 | MIT; .NET Foundation and Contributors |
| Microsoft.Win32.SystemEvents | 10.0.12 | MIT; .NET Foundation and Contributors |
| Microsoft.Web.WebView2 SDK | 1.0.2903.40 | Microsoft BSD-style redistribution terms; exact [LICENSE](licenses/Microsoft.Web.WebView2-LICENSE.txt) and [NOTICE](licenses/Microsoft.Web.WebView2-NOTICE.txt) retained |
| xunit / xunit.runner.visualstudio (tests only) | 2.9.3 / 3.1.4 | Apache-2.0 |
| Microsoft.NET.Test.Sdk / coverlet.collector (tests only) | 17.14.1 / 6.0.4 | MIT |

## Microsoft WebView2 Runtime

TeraBox website sign-in uses the separately installed Microsoft WebView2 Runtime. It is not included as a complete browser runtime in the portable ZIP. The optional setup EXE uses Microsoft's signature-checked Evergreen bootstrapper when needed. Runtime redistribution/use and updates are subject to Microsoft's terms; see [WebView2 distribution](https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution). The SDK license above must not be presented as the runtime's license.

## .NET runtime (self-contained releases)

The verified self-contained release uses Microsoft.NETCore.App and Microsoft.WindowsDesktop.App **10.0.13**, with MIT licenses and third-party notices. Exact restored runtime-pack texts are retained in [runtime LICENSE](licenses/DotNet-Runtime-LICENSE.txt), [runtime THIRD-PARTY-NOTICES](licenses/DotNet-Runtime-THIRD-PARTY-NOTICES.txt) and [WindowsDesktop runtime LICENSE](licenses/DotNet-WindowsDesktop-Runtime-LICENSE.txt). Refresh these from the actual runtime packs when the shipped runtime changes. Copyright .NET Foundation and Contributors. The exact MIT text and the restored WindowsDesktop SDK's notice set are retained in [DotNet-MIT-LICENSE.txt](licenses/DotNet-MIT-LICENSE.txt) and [DotNet-WindowsDesktop-THIRD-PARTY-NOTICES.txt](licenses/DotNet-WindowsDesktop-THIRD-PARTY-NOTICES.txt). The latter is identified by its source and is not claimed as an exhaustive audit of every transitive dependency in every future release. Runtime-pack notice files, when emitted by publishing, must also be retained.


## Included source and product licensing

The exact MIT grant and required copyright attribution for included source are retained in [Source-MIT-NOTICE.txt](licenses/Source-MIT-NOTICE.txt). New product terms have not been supplied by the owner; see [LICENSE](LICENSE). The product license placeholder does not revoke included source rights or relicense vendors.

Release packaging includes the root license-status file, source attribution, all vendor license texts and upstream rclone COPYING. Installer EXE acceptance is incomplete; optional prerequisite bundling describes its build path rather than a verified installation.
