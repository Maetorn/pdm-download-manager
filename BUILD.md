# Building PDM

## Requirements

- Windows 10/11 x64
- .NET 10 SDK — either installed normally (`dotnet` on PATH) or in `%USERPROFILE%\.dotnet` (dotnet-install script).
- Windows PowerShell 5.1 (built in). It is also used to draw the icons when they are missing.
- Internet access to `api.nuget.org` for the first build only: restore pulls Microsoft's runtime packs (self-contained publish) and the trimmer. `nuget.config` limits sources to nuget.org and to `Microsoft.*` / `runtime.*` package IDs. The projects have **no** third-party package references.

## One command

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

Options: `-SkipTests` skips the test run.

What it does:

1. Draws the icons with `tools\make-icons.ps1` if any are missing.
2. Publishes `src\PDM.NativeHost` (trimmed, single file) to `build\host\PDM.NativeHost.exe`.
3. Zips `extension\opera` to `build\extension.zip`.
4. Builds and runs `tests\PDM.Tests` (console runner with its own local HTTP server), passing the host exe so the native-host tests run too. A failing test stops the build.
5. Publishes `src\PDM` (Release, self-contained, single file). The host exe and the extension zip are embedded as resources (`PDM.NativeHost.exe`, `extension.zip`).
6. Copies the results to `dist\`: `PDM.exe`, `PDM-Setup.exe` (same file; the name starts setup mode), `extension\opera`. SHA-256 of each file is printed.

## Projects

| Project | Purpose |
| --- | --- |
| `src/PDM.Core` | Engine (`HttpDownloader`, `DownloadManager`), IPC (named pipe + bridge protocol), native host registration, security helpers, yt-dlp/FFmpeg tool manager |
| `src/PDM` | WPF app (UI built in code, no XAML), tray (WinForms `NotifyIcon`), setup/uninstall |
| `src/PDM.NativeHost` | Native messaging host started by Opera; forwards messages to the app's pipe |
| `tests/PDM.Tests` | Console test runner |
| `extension/opera` | MV3 extension (fixed ID `dloanbeiidcfnnmdmgpcdhbkenjjangb` via the manifest `key`) |

## Command-line switches of PDM.exe

| Switch | Effect |
| --- | --- |
| *(exe name contains "Setup")* or `--install` | Setup window |
| `--uninstall` | Uninstall (asks first) |
| `--tray` | Start hidden in the tray |
| `--exit` | Ask the running instance to exit |
| `<http(s) URL> …` | Add download(s); forwarded to the running instance if there is one |

## Compile check on Linux

WPF cannot run on Linux, but the code compiles there:

```bash
dotnet build src/PDM -p:EnableWindowsTargeting=true
dotnet build tests/PDM.Tests -p:EnableWindowsTargeting=true
```

The tests themselves need Windows (DPAPI, named pipe ACLs, Attachment Services).
