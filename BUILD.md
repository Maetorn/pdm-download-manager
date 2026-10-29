# Building PDM

## Requirements

- Windows 10/11 x64
- .NET 10 SDK — either installed normally (`dotnet` on PATH) or in `%USERPROFILE%\.dotnet` (dotnet-install script).
- **Windows C++ build tools** (Visual Studio or "Build Tools for Visual Studio" with the "Desktop development with C++"
  workload): the native messaging host is compiled with NativeAOT, which needs the platform linker. Without it
  `build.ps1` stops at the "Publishing native host" step.
- Windows PowerShell 5.1 (built in). It is also used to draw the icons when they are missing.
- Internet access to `api.nuget.org` for the first build only: restore pulls Microsoft's runtime packs (self-contained publish) and the trimmer. `nuget.config` limits sources to nuget.org and to `Microsoft.*` / `runtime.*` package IDs. The projects have **no** third-party package references.

## One command

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

Options: `-SkipTests` skips the test run; `-Version 1.2.5` stamps the app and the extension (local builds are 1.0.0, CI passes its run number).

What it does:

1. Draws the icons with `tools\make-icons.ps1` if any are missing.
2. Publishes `src\PDM.NativeHost` compiled to machine code (NativeAOT) to `build\host\PDM.NativeHost.exe`.
3. Zips `extension\opera` to `build\extension.zip` and packs the Firefox add-on (`extension\firefox\manifest.json` + the same files) to `build\firefox.xpi`; with `AMO_JWT_ISSUER`/`AMO_JWT_SECRET` set it is signed by Mozilla (unlisted).
4. Builds and runs `tests\PDM.Tests` (console runner with its own local HTTP server), passing the host exe so the native-host tests run too. A failing test stops the build.
5. Publishes `src\PDM` (Release, self-contained, single file). The host exe, the extension zip and the Firefox add-on are embedded as resources.
6. Copies the results to `dist\`: `PDM.exe`, `PDM-Setup.exe` (same file; the name starts setup mode), `PDM.exe.sha256` (used by the updater), `extension\opera`, `extension\firefox\pdm-firefox.xpi`. SHA-256 of each file is printed.

## Projects

| Project | Purpose |
| --- | --- |
| `src/PDM.Core` | Engine (`HttpDownloader`, `DownloadManager`), IPC (named pipe + bridge protocol), native host registration, security helpers, yt-dlp/FFmpeg tool manager |
| `src/PDM` | WPF app (UI built in code, no XAML), tray (WinForms `NotifyIcon`), setup/uninstall |
| `src/PDM.NativeHost` | Native messaging host started by the browser (Opera, Chrome, Edge, Firefox); forwards messages to the app's pipe |
| `tests/PDM.Tests` | Console test runner |
| `extension/opera` | MV3 extension for Opera, Chrome and Edge (fixed ID `dloanbeiidcfnnmdmgpcdhbkenjjangb` via the manifest `key`); strings in `_locales` (en, de, fr, it, tr) |
| `extension/firefox` | Firefox manifest (add-on ID `pdm-download-manager@maetorn`); the other files come from `extension/opera` |

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

The tests also run there (`dotnet run --project tests/PDM.Tests`). A few need Windows (DPAPI, named pipe ACLs, path
rules) and a few need a tool that may be missing (FFmpeg, the native host exe, a script as yt-dlp on Linux only). The
runner reports those as **skipped**, separately from passed and failed; they are not counted as passes. CI runs
almost all of them on Windows (the Linux-only script test and the FFmpeg conversion are skipped there).

## Screenshots of every window (CI)

`.github/workflows/ui-tour.yml` builds a test version (`dotnet build src/PDM -c Release -p:UiTour=true`, which adds
`src/PDM/UiTour.cs`; releases never include it) and runs `PDM.exe --ui-tour <folder> <language> <theme>` for every
language in light and dark. Each run saves a PNG of every window and `clipped.txt`, the list of texts that do not fit
their space. The results are pushed to the `ui-tour-results` branch, and the workflow fails when a run has missing
screenshots, a report line other than "window captured", or fewer than the 10 language/theme folders.
