# PDM — Project Overview

A complete description of PDM for reviewers (human or AI): what it does, how it is built, where every piece lives,
which security decisions were made and why, how it is tested, and what is still unverified.
User-facing documentation is in [README.md](../README.md); build details in [BUILD.md](../BUILD.md); the test log
in [TEST_RESULTS.md](../TEST_RESULTS.md).

---

## 1. What PDM is

PDM is a download manager for Windows 10/11 (x64), comparable in scope to commercial download managers:

- segmented (multi-connection) HTTP downloads with pause/resume,
- a browser extension (Opera, Chrome, Edge, Firefox) that hands browser downloads and videos to the app,
- video downloads through yt-dlp (optional tools, installed on request),
- queues with schedules, history/statistics, site grabber, archive extraction, MP3/MP4 conversion,
- automatic self-update from this repository's GitHub releases.

Repository: `github.com/Maetorn/pdm-download-manager` (public, MIT). Releases are built by CI on every merge to
`main` and published as `v1.2.<run number>` with `PDM-Setup.exe`, `PDM.exe`, `PDM.exe.sha256`.

### Hard project rules

| Rule | Consequence in the code |
| --- | --- |
| **No third-party packages** | Only .NET 10 and Windows components. `nuget.config` allows only `Microsoft.*` / `runtime.*` packages from nuget.org. JSON via System.Text.Json source generation, HTTP via `SocketsHttpHandler`, zip via `System.IO.Compression`, 7z/rar/tar via Windows' own `tar.exe`. |
| **Security first** | Every input from the browser, servers, files and tools is treated as untrusted (see §6). |
| **Must not slow down the PC or the browser** | Idle work is skipped, the native host is NativeAOT, disk writes are batched, the extension only listens to narrow events (see §8). |
| **Never claim untested things passed** | TEST_RESULTS.md marks anything not run as **MANUAL**. |
| **Everything in English in the repo; UI in 5 languages** | en, de, fr, it, tr for the app and the extension. |

---

## 2. Technology

| Area | Choice |
| --- | --- |
| Language / runtime | C# on .NET 10 (`net10.0-windows`), nullable enabled |
| UI | WPF built entirely in code (no XAML), WinForms only for the tray icon |
| App packaging | Self-contained single-file `PDM.exe` (~175 MB, uncompressed for faster start, ReadyToRun) |
| Native messaging host | Separate project compiled with NativeAOT (`PDM.NativeHost.exe`, starts in milliseconds) |
| Extension | Manifest V3, plain JavaScript, no build step, no libraries |
| IPC app ↔ host | Per-user named pipe, length-prefixed JSON frames |
| Tests | Console test runner (`tests/PDM.Tests`) with its own local HTTP test server |
| CI | GitHub Actions on `windows-latest` (build, tests, release) + a UI screenshot tour |

---

## 3. Repository map

```
.github/workflows/build.yml     Build + tests on Windows; release job (main only) publishes the GitHub release
.github/workflows/ui-tour.yml   Screenshots of every window in 5 languages × light/dark (test build only)
build.ps1                       One-command build: icons → native host → extension zip/xpi → tests → app → dist\
tools/make-icons.ps1            Draws the icons (PowerShell + System.Drawing) when missing
nuget.config                    Official feed only, Microsoft packages only

src/PDM.Core/                   Engine and everything without UI (also used by the tests)
  AppPaths.cs                   Data folders (%LOCALAPPDATA%\PDM\…), pipe/mutex names per user SID, version
  Hardening.cs                  Process mitigations at start (DLL search, image-load policy)
  ToolJob.cs                    Windows job objects around helper tools
  Archives.cs                   Zip extraction (.NET) and 7z/rar/tar (Windows tar.exe), MOTW propagation
  BatchPattern.cs               "file[001-120].jpg" / "[a-z]" expansion
  DiskSpace.cs                  Free-space checks, disk-full error code
  History.cs                    Finished downloads + per-day totals (history.json), debounced saves
  ListTransfer.cs               Export/import of the list (.txt links / .json)
  Log.cs                        File logger with secret scrubbing, 7-day retention
  NativeFile.cs                 Preallocated file handles for segmented writes
  PdmJson.cs                    System.Text.Json source-generation context
  RecycleBin.cs                 Delete to Recycle Bin
  SiteGrabber.cs                Breadth-first crawl of pages for file links (http/https HTML only, size caps)
  Store.cs                      Atomic JSON save with .bak (settings.json, downloads.json, history.json)
  Engine/
    AddRequest.cs               What a new download asks for (URL, name, folder, cookies, referrer, queue…)
    Cookies.cs                  Cookie data from the extension; per-host container rules
    DownloadManager.cs          The download list, queues, scheduling of jobs, retries, history, events
    HttpDownloader.cs           One URL: probe, up to 16 range connections, dynamic segment splitting, resume
    NetConfig.cs                Proxy (system/none/custom HTTP or SOCKS) and per-site logins (Basic/Digest)
    SpeedLimiter.cs             Token bucket (global and per download)
  Ipc/
    BridgeProtocol.cs           Parsing/validation of messages from the native host (untrusted input)
    NativeHostRegistration.cs   HKCU registry + host manifests for Chromium browsers and Firefox
    PipeFraming.cs              [uint32 LE length][UTF-8 JSON] framing
    PipeServer.cs               Named pipe server with a current-user-only ACL
  Models/
    AppSettings.cs              All settings, queues, versioned migration (Normalize)
    DownloadItem.cs             One download (INotifyPropertyChanged), segments, video options
    FileCategories.cs           Extension → category (Compressed, Documents, Music, Programs, Video, Images, Other)
  Security/
    AttachmentScanner.cs        Mark-of-the-Web + antivirus scan via Windows Attachment Services
    Dpapi.cs                    Per-user encryption of cookies/passwords
    FileNames.cs                Safe file names, Content-Disposition parsing, URL redaction for logs
  Update/UpdateClient.cs        Reads the latest GitHub release, downloads PDM.exe, verifies SHA-256
  Video/
    ToolManager.cs              Installs/updates yt-dlp, FFmpeg, Deno from official GitHub releases (SHA-256)
    YtDlpRunner.cs              Runs yt-dlp (argument list, no shell, config ignored), cookie strategy, retries
    YtDlpRunner.Direct.cs       Multi-connection path: yt-dlp resolves formats, HttpDownloader fetches, FFmpeg merges
    MediaConverter.cs           FFmpeg conversion to MP3/MP4 with progress

src/PDM/                        WPF app
  Program.cs                    Entry: hardening, setup/uninstall switches, single instance (mutex), update wait
  App.cs                        PdmApp: startup, bridge message handling, tray, schedule timer, clipboard monitor
  MainWindow.cs                 Toolbar, category/queue sidebar, download list (DataGrid), menus, rubber-band select
  AddDownloadWindow.cs          Single link (live probe) or batch checklist; duplicate prompt; login prompt on 401
  ProgressWindow.cs             Per-download progress window + "Download complete" window
  DetailsWindow.cs              Segment map, connections slider, refresh address, SHA-256
  VideoWindow.cs                Media links from the page, or yt-dlp qualities; tool installation
  SettingsWindow.cs             9 tabs: General, Files, Connection, Site logins, Browser, Security, Video, Updates, About
  SchedulerWindow.cs            Queues and schedules; CountdownWindow before sleep/shutdown
  HistoryWindow.cs              Statistics tiles, 30-day chart, history list
  GrabberWindow.cs              Site grabber UI
  ConvertWindow.cs              MP3/MP4 conversion UI
  LoginDialog.cs                Site login editor
  SetupWindow.cs                Installer UI; ExtensionGuideWindow (how to load the extension per browser)
  Installer.cs                  Per-user install/uninstall (no admin)
  UpdateService.cs              Update timer, download, swap-on-idle, restart
  Tray.cs                       Notification-area icon, menu, balloons
  Theme.cs                      Light/dark brushes and implicit styles
  Ui.cs                         PdmWindow base class and small UI helpers
  Native.cs                     Win32 calls (dark title bar, foreground, shell icons, power requests)
  Loc.cs                        UI strings: Turkish + English table, language selection ("auto" = Windows language)
  LocDe.cs / LocFr.cs / LocIt.cs  German / French / Italian strings (missing key → English)
  UiTour.cs                     TEST BUILD ONLY (#if UI_TOUR): opens every window, saves PNGs, reports clipped text

src/PDM.NativeHost/Program.cs   Native messaging host (stdin/stdout ↔ named pipe)

extension/opera/                Extension for Opera, Chrome, Edge (fixed ID via manifest "key")
  manifest.json                 MV3, service worker, permissions (see §6.4)
  background.js                 Download capture, context menus, media sniffing, native messaging
  content.js                    Video button (closed shadow root), link collection for "all links"
  popup.* / options.* / grabber.*  Extension pages; i18n.js fills text from _locales
  _locales/{en,de,fr,it,tr}     Extension strings
extension/firefox/manifest.json Firefox manifest (event page, add-on ID); other files shared with extension/opera

tests/PDM.Tests/                Console test runner (61 tests on Windows) and TestServer.cs (local HTTP server)
docs/PROJECT_OVERVIEW.md        This file
```

---

## 4. Architecture

```
 Browser (Opera/Chrome/Edge/Firefox)
   └─ PDM extension (background.js, content.js)
        │ chrome.runtime.sendNativeMessage("com.pdm.host", json)
        ▼
 PDM.NativeHost.exe  (started by the browser per message port; NativeAOT)
   - checks the caller: chrome-extension://<32 a-p>/ origin or the Firefox add-on ID
   - wraps the message: {"origin": "...", "msg": {...}}
        │ named pipe  \\.\pipe\PDM.ipc.<user SID>   (current user only, no network logons)
        ▼
 PDM.exe (WPF app, single instance per user)
   PipeServer → BridgeProtocol.Parse (allow-listed origins, types, sizes, http(s) only)
             → App.HandleBridgeAsync → AddDownloadWindow / VideoWindow / DownloadManager
   DownloadManager ──► HttpDownloader (segments) ──► file (preallocated, .pdmdl temp name)
                   └─► YtDlpRunner (video) ──► yt-dlp / FFmpeg / Deno (in job objects)
   Store (settings.json, downloads.json, history.json)   Log (logs\pdm-YYYYMMDD.log)
   UpdateService ──► api.github.com releases ──► PDM.exe + SHA-256
```

### 4.1 Threading model

- `DownloadManager` and every `DownloadItem` are only touched on the owner (UI) thread.
- The engine (`HttpDownloader`, tool runners) works on its own copies and posts results back through the
  `SynchronizationContext`.
- The manager's 1-second tick is skipped when nothing is active. The main window does no list work while hidden.

### 4.2 Data on disk (`%LOCALAPPDATA%\PDM`)

| Path | Content |
| --- | --- |
| `data\settings.json` | `AppSettings` (versioned; `Normalize()` migrates old files, e.g. v3 moved the scheduler into queues) |
| `data\downloads.json` | Download list with segment state for resume |
| `data\history.json` | Finished downloads and per-day totals |
| `logs\` | Daily logs, 7 days kept, secrets scrubbed |
| `host\` | Native host exe + manifests (Chromium and Firefox) + `app-path.txt` |
| `extension\`, `firefox\` | Unpacked extension / `.xpi`, re-extracted when the version changes |
| `tools\` | yt-dlp, FFmpeg, Deno (only if installed by the user) |

Saves are atomic (temp file → `File.Replace` with `.bak`).

### 4.3 Download engine (`HttpDownloader`)

1. **Probe:** a range request learns the size, range support, ETag/Last-Modified, final URL after redirects and
   the file name (Content-Disposition, RFC 5987 `filename*`).
2. **Download:**
   - The file is preallocated under a temporary name.
   - Up to N connections (1–16) each fetch a byte range.
   - When a connection is free, the largest remaining range is split in half (dynamic segmentation).
3. **Write path:** reads are gathered into one disk write per ≤128 KB or every 250 ms (ArrayPool buffers).
4. **Resume:** segment positions are saved. On restart, a changed ETag, Last-Modified or size means the file is
   restarted from zero.
5. **Errors:**
   - Transient errors (connection drop, 429/503 with Retry-After) are retried with backoff.
   - With auto-retry on, failed downloads come back when the network returns.
6. **Limits:** speed limiting through a shared token bucket, globally and per download.
7. **Bounded requests** (YouTube-style servers that only serve limited ranges) are supported.
8. **Finish:** rename to the final name (auto-rename on conflict), Attachment Services (Mark-of-the-Web + AV
   scan), optional SHA-256 verification, history record, optional auto-extract.

### 4.4 Video

- **Direct media** found by the extension (webRequest headers, `<video>` sources) is downloaded like any file.
- **yt-dlp path:**
  - `YtDlpRunner` runs yt-dlp with an argument list (no shell), `--ignore-config` and `--` before the URL.
  - The format selection prefers H.264/AAC MP4 and avoids AV1, so files play in Windows' own player.
- **Multi-connection path** (`YtDlpRunner.Direct`): yt-dlp only resolves the formats (`-J`), PDM downloads the
  picture and sound streams with several connections, and FFmpeg merges them without re-encoding. It falls back
  to yt-dlp's own downloader when that is not possible. Progress is saved, so pause/resume works.
- **Cookies:**
  - YouTube is tried *without* browser cookies first, because signed-in clients need a JS runtime; other sites
    are tried with cookies first.
  - On failure the other choice is tried once.
  - Cookies are written to a temporary Netscape file that is deleted afterwards.
- **Deno** is installed with the tools and passed as `--js-runtimes deno:<path>`. yt-dlp runs it sandboxed
  (`--no-prompt --no-remote`, no `--allow-*`).
- **Tools:**
  - yt-dlp and Deno come from their own projects' GitHub releases; the Windows FFmpeg build comes from
    `yt-dlp/FFmpeg-Builds` (not from the FFmpeg project). Each file is verified against the SHA-256 list in the same
    release, which proves it is that release's file, not who built it.
  - yt-dlp and Deno are kept up to date in the background (every 12 h, skipped while a video job runs).

### 4.5 Self-update (`UpdateService`, `UpdateClient`)

- **When:** 3 minutes after start, then every 6 hours, or on demand (Settings → Updates → Check now).
- **Check:** `GET https://api.github.com/repos/Maetorn/pdm-download-manager/releases/latest` with no token (the
  repository is public).
- **Accepted:** only a newer version, only assets whose API URL belongs to this repository, a size cap of 400 MB,
  and `PDM.exe.sha256` must match the downloaded bytes.
- **Install:**
  - Only when no download is running: rename `PDM.exe` → `PDM.exe.old`, new file → `PDM.exe`, start it with
    `--wait-pid <old pid> --updated`, exit.
  - The new instance deletes `.old`.
- **Migration:** an old saved GitHub token (from when the repository was private) is dropped by
  `AppSettings.Normalize()`.

### 4.6 Browser integration

- **Capture in Chromium browsers:** `downloads.onDeterminingFilename`.
- **Capture in Firefox:** `downloads.onCreated`, then pause → ask PDM → cancel and erase if PDM accepted, otherwise
  resume.
- **Rule:** a browser download is cancelled only *after* PDM confirmed it took it over. If PDM is unreachable,
  the browser keeps the download.
- **Filters:** file extensions, minimum size and excluded sites are configurable in the extension options.
  "Let the browser handle the next download" skips one capture.
- **Context menu:** download link, media, selected links, all links (filtered list page), this page's video.
- **Video button:** a small tab over videos (≥200×120), rendered in a closed shadow root. Only real (`isTrusted`)
  clicks count.
- **Extension updates:** when PDM updates, it re-extracts the extension. The extension sees PDM's version in
  replies and reloads itself (Firefox: the user approves the update of the signed `.xpi`).
- **Native host registration:**
  - HKCU only: Chrome/Chromium keys (Opera reads these), the Edge key and the Mozilla key.
  - Each manifest allows only PDM's fixed extension ID (`dloanbeiidcfnnmdmgpcdhbkenjjangb`) or the Firefox
    add-on ID (`pdm-download-manager@maetorn`).

### 4.7 Localization

- **App:**
  - `Loc.T(key)` covers 369 keys.
  - The Turkish + English table is in `Loc.cs`; German, French and Italian are in `LocDe/Fr/It.cs`.
  - A missing key falls back to English.
  - Settings → General → Language offers Automatic (the Windows display language, English if unsupported) or a
    fixed language. It takes effect after restart.
  - `CultureInfo.CurrentUICulture` is set too, so yt-dlp subtitles default to the app language plus English.
- **Extension:** `_locales/{en,de,fr,it,tr}/messages.json`; English is the default locale.
- **Test:** the "Languages" test checks that every key exists in every language and that the `{0}` / `$A$`
  placeholders match.

---

## 5. Features (user view)

- **Downloads:**
  - 1–16 connections, pause and resume (also after a restart or power cut), "Refresh address" for expired links.
  - Speed limits, global and per download.
  - Auto retry, and the computer is kept awake while downloading.
  - Disk-space check, duplicate detection, batch patterns, "Check links" in batch lists.
- **Organisation:**
  - Categories and subfolders by type (`Downloads\Compressed`, `Documents`, `Music`, `Programs`, `Video`).
  - Search, drag & drop.
  - Multiple named queues with start/stop times, days, parallelism and an after-queue action (exit, sleep,
    shutdown, with a 60 s countdown).
- **Video:** direct media; yt-dlp with quality, container, audio-only, subtitles and playlists; conversion to
  MP3/MP4.
- **Other:**
  - Archive auto-extraction (zip, 7z, rar, tar), off by default.
  - History and statistics, export/import of the list, site grabber.
- **Network:** system, direct or custom proxy (HTTP/SOCKS5, optional login); per-site logins (Basic over https
  only, or Digest).
- **App:** tray mode, notifications, light/dark theme, five languages, per-user setup/uninstall, automatic
  updates.

---

## 6. Security design

### 6.1 Trust boundaries

| Source | Treated as | Main defences |
| --- | --- | --- |
| Web pages | Hostile | Extension content script in a closed shadow root, `isTrusted` clicks, no page data trusted beyond URLs |
| Extension → host | Untrusted input | Host accepts only PDM's extension ID / Firefox add-on ID; JSON depth limit; 8 MB frame cap |
| Pipe clients | Untrusted input | Pipe ACL: current user only, network logons denied; `FirstPipeInstance`; client checks server ownership (`CurrentUserOnly`); `BridgeProtocol` re-validates origin, types, counts (≤500 items), string lengths (≤16 KB), only http(s) URLs |
| Servers | Hostile | Certificate validation always on; file names sanitized; size/redirect limits; cookies sent only to their own host across redirects |
| Downloaded files | Hostile | Never run automatically; Mark-of-the-Web + AV scan; warning before opening executables (long list incl. .chm, .msc, .library-ms, .appinstaller …) |
| Archives | Hostile | Extracted only into a new folder next to the archive; `..`/absolute/link escapes refused; size checked; MOTW copied to the extracted files |
| Media files / tool output | Hostile | FFmpeg, tar run in job objects that forbid child processes and UI access; yt-dlp has no shell, ignores config files |
| Tools from GitHub | Verified | Official release assets only, SHA-256 checked against the release's checksum file |
| App updates | Verified | Public GitHub release of this repo only, SHA-256, newer version only |

### 6.2 Process hardening (`Hardening.cs`, `ToolJob.cs`, `Program.cs`)

- `[DefaultDllImportSearchPaths(System32)]` in all three assemblies.
- `SetDllDirectory("")` and the image-load mitigation policy: no remote images, no low-integrity images,
  System32 first. A planted DLL next to `PDM-Setup.exe` in Downloads is never loaded.
- Job objects for helper tools:
  - kill on job close and on unhandled exceptions;
  - an active-process limit of 1 for FFmpeg and tar;
  - UI restrictions (no clipboard, desktop, system parameters, display settings, global atoms, exit-windows).
  - **Limits:** this is not a file-system or network sandbox. The process is put in the job right after it starts,
    and if that fails PDM logs it and continues. No isolation guarantee is claimed.
- The native host grants `AllowSetForegroundWindow` only to the PID at the other end of the pipe
  (`GetNamedPipeServerProcessId`).

### 6.3 Secrets

- **Browser cookies:** DPAPI-encrypted (current user) while a download needs them, deleted when it finishes.
- **Site and proxy passwords:** DPAPI-encrypted in settings.
  - A site password is sent only to that host, and Basic only over https or loopback.
  - The proxy password is passed to yt-dlp via environment variables, not the command line.
- **Logs:** no cookies, headers, query strings or passwords. `FileNames.RedactUrlsIn` removes URLs from tool
  error texts, and `Log` scrubs obvious secrets again.
- **The app contains no keys:** updates use the public API anonymously, and CI uses the short-lived
  `GITHUB_TOKEN`.

### 6.4 Extension permissions

`downloads, nativeMessaging, contextMenus, cookies, storage, webRequest, tabs` plus `host_permissions: <all_urls>`.
Each is needed:

| Permission | Why |
| --- | --- |
| `downloads` | Capture downloads |
| `cookies` | Logged-in downloads (only when the "send cookies" option is on) |
| `webRequest` | Media sniffing, narrowed to media/object types and media URL patterns, headers only |
| `tabs` | Per-tab media lists and the page URL for videos |
| `<all_urls>` | The content script runs on all pages for the video button |

There is no remote code, no `eval`, and no network requests from the extension except native messaging.

### 6.5 CI / supply chain

- **Build job:** `contents: read`, and checkout without persisted credentials. It runs the build and tests, so it
  must not be able to write.
- **Release job** (main only): `contents: write`. It downloads the tested artifact, re-checks `PDM.exe` against
  `PDM.exe.sha256`, publishes the release and keeps the 5 newest.
- **Workflows** run on `push` only (fork PRs get no secrets or write token). Only GitHub's and Microsoft's own
  actions are used, plus a pinned `web-ext@10.7.0` for optional Firefox signing.
- **UI tour workflow:** `contents: write` only to force-push screenshots to the `ui-tour-results` branch.
- **Repository settings:**
  - Secret scanning and push protection, private vulnerability reporting, Dependabot alerts, CodeQL (C#,
    JS, Actions).
  - A ruleset on `main` blocks force pushes and deletion. It does **not** require pull-request review or passing
    checks, so the account owner can still ship an update directly (see §9).
  - Outside contributors need approval to run workflows; the default workflow token is read-only.

---

## 7. Build, test, release

- **Local build:** `powershell -ExecutionPolicy Bypass -File build.ps1`. The output goes to `dist\` (see
  BUILD.md).
- **Tests:** `tests/PDM.Tests`, a console runner, 61 tests on Windows. They cover:
  - file names, bridge validation, Firefox manifests, languages;
  - yt-dlp arguments and format choice, cookies, updater parsing;
  - batch patterns, retries, disk space, security helpers, settings migration;
  - queues, archives, history, export/import, proxy and site logins, the grabber, conversion;
  - an end-to-end engine against the local test server (parallel, no-range, bounded, drops, pause/resume,
    changed file, 429, 404/403, chunked, redirects, cookies, speed limit);
  - the manager end to end, job objects, IPC, and the native host exe.
  - On Linux all but 4 Windows-only tests run.
- **UI tour:** `ui-tour.yml` builds with `-p:UiTour=true` and runs `PDM.exe --ui-tour <dir> <lang> <theme>` for
  5 languages × light/dark. It produces 270 screenshots and a report of text that is cut, clipped by a parent or
  below 3:1 contrast. The last run reported none.
- **Release:** every push to `main` builds, tests and publishes `v1.2.<run>`. Installed apps update themselves.

---

## 8. Performance decisions

- The native host is NativeAOT: no JIT, a few ms per browser message.
- The manager tick runs only while something is active. The main window skips list work while hidden or
  minimized, and the browser-bridge status is cached.
- When idle in the tray the app runs a GC and trims its working set. GC is non-concurrent and workstation.
- Disk writes are batched (≤128 KB / 250 ms). Buffers come from `ArrayPool`.
- `PDM.exe` is uncompressed single-file with ReadyToRun, so it maps straight from disk and starts faster.
- The extension uses narrow `webRequest` filters. The content script has no timers while idle, handles mouse
  moves once per frame, and only looks at videos when the page has any.

---

## 9. Known limitations and open items

- **Not code-signed:** SmartScreen warns on first run ("More info → Run anyway").
- **Firefox add-on is unsigned** unless `AMO_JWT_ISSUER`/`AMO_JWT_SECRET` are set in CI. Release Firefox then only
  loads it temporarily.
- **Chromium extension is loaded unpacked** (developer mode). It is not published in a store.
- **Trust model of updates:** anyone who controls the GitHub account or the `main` branch can ship an update to
  every user. Mitigated by 2FA, branch rules and the separate release job. There is no offline signing key.
- **DRM-protected video is not supported** (by design).
- **Browser capture limits:**
  - A download that follows a POST (a form) stays in the browser. PDM can only repeat a GET. This is decided from
    POST requests the extension saw in the last 5 minutes. A POST that is answered by a redirect to a different
    file address is not recognised.
  - Firefox container tabs send their own container's cookies, but cookies of first-party-isolated (partitioned)
    storage are not collected.
  - At most 500 links go in one send from the link list; the page shows how many were handed over and keeps the rest
    ticked.
- **MANUAL items** in TEST_RESULTS.md: real-browser flows, sleep/shutdown actions, high-DPI, language switching
  through Settings, uninstall message boxes, the extension in German/French/Italian browsers.

---

## 10. Suggested review focus

Areas where a careful second look is most valuable:

1. **`src/PDM.Core/Ipc/BridgeProtocol.cs`, `PipeServer.cs`, `src/PDM.NativeHost/Program.cs`:** can any local
   process or web page make PDM download to an arbitrary path, run something, or read data?
2. **`src/PDM.Core/Security/FileNames.cs`, `Archives.cs`:** path traversal, reserved names, Unicode tricks,
   archive escapes.
3. **`src/PDM.Core/Engine/HttpDownloader.cs`:** segment arithmetic, resume correctness, races between
   connections, ETag/size change handling, cookie scoping across redirects.
4. **`src/PDM.Core/Engine/DownloadManager.cs`:** queue scheduling, retry timing, state persistence, thread
   affinity.
5. **`src/PDM.Core/Video/*`:** yt-dlp argument construction, temporary cookie files, tool installation and
   checksum parsing.
6. **`src/PDM.Core/Update/UpdateClient.cs`, `src/PDM/UpdateService.cs`:** release parsing, size cap, hash check,
   exe swap and rollback.
7. **`extension/opera/background.js`:** the "cancel only after PDM confirmed" rule, the Firefox capture path,
   message validation from content scripts and pages.
8. **`.github/workflows/*.yml`:** token permissions, what runs with write access.
9. **UI (`src/PDM/*Window.cs`, `Theme.cs`):** dark theme and the five languages (the UI tour covers the layout;
   behaviour is mostly MANUAL).

Constraints for any proposed change: no third-party packages, keep the hot paths cheap, keep every user-facing
string in all five languages (the Languages test enforces this), and update TEST_RESULTS.md honestly.
