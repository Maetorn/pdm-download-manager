# PDM — Download Manager for Windows

PDM is a fast, lightweight download manager for Windows with a browser extension for Opera, Chrome, Edge and Firefox.
The app itself uses no third-party libraries — only .NET 10 and components that ship with Windows. The optional
video features download separate programs (yt-dlp, FFmpeg, Deno) when you ask for them, see "License" below.

The interface is available in **English, German, French, Italian and Turkish** (it follows the Windows display language;
change it in Settings → General).

## Features

- Segmented downloads with 1–16 connections per file; pause and resume where it stopped
- Several named queues ("Download Later"), each with its own scheduler (start/stop time, days, files at the same time);
  sleep or shut down when a queue finishes (with a 60 s countdown you can cancel)
- Categories (Compressed, Documents, Music, Programs, Video, Images), search, drag and drop, Ctrl+V to add links
- Catches download links copied to the clipboard (click the notification to add)
- Details window: live segment map, change the number of connections on the fly, "Refresh address" for expired links,
  SHA-256 compute/verify
- Browser extension (Opera, Chrome, Edge, Firefox): hands downloads to PDM, context menu, all links on a page, video button
- Video: direct media files on the page, or yt-dlp for YouTube and similar sites (optional, verified with SHA-256).
  yt-dlp only finds the stream addresses; picture and sound download with PDM's multi-connection engine and FFmpeg
  merges them (falling back to yt-dlp's own downloader). yt-dlp uses Deno for YouTube's JavaScript checks. yt-dlp and
  Deno are kept up to date automatically.
- The computer does not go to sleep while downloading; downloads are retried automatically when the connection drops
  (right away when it comes back)
- Disk space check, warning for addresses already in the list, per-download and global speed limits
- Batch addresses: `https://site/image[001-120].jpg` or `[a-z]` expands into all addresses
- "Check links" in batch lists: each link's size; links that fail are unticked
- Automatic extraction of downloaded archives (zip, 7z, rar, tar — optional, off by default; right click → "Extract archive")
- Export/import the list (.txt link list or .json), history and statistics (total, today, last 7/30 days, daily chart)
- Site grabber (More → "Site grabber…"): follows linked pages to a chosen depth and lists the files (e.g. only zip, pdf)
- Convert: right click a finished video/audio file → Convert → MP3 or MP4 (H.264/AAC; streams that already fit are copied)
- Proxy (Windows setting, direct, or an HTTP/SOCKS5 address with optional login) for downloads, video tools and updates
- Site logins for sites that ask for a username and password (HTTP Basic/Digest), stored encrypted
- Light on resources: frees memory when idle in the tray, does no list work while the window is hidden, batches small
  network reads into one disk write; the browser bridge (native host) is compiled to machine code and starts in milliseconds
- Tray icon, notifications, light/dark theme

## Download

Get `PDM-Setup.exe` from the [latest release](https://github.com/Maetorn/pdm-download-manager/releases/latest) and run it.
(`PDM.exe` is the same program without the setup step; it is what the automatic updater uses, with `PDM.exe.sha256`.)

PDM is not code-signed, so Windows SmartScreen may say "Windows protected your PC" the first time.
Click **More info → Run anyway**.

## Install

1. Run `PDM-Setup.exe` (or `dist\PDM-Setup.exe` if you built it yourself). No administrator rights are needed.
2. Choose the desktop shortcut, start with Windows and browser integration, then click **Install**.
   PDM installs to `%LOCALAPPDATA%\Programs\PDM`, adds a Start menu entry and appears in "Apps & features".
3. Click **Launch PDM**.

To uninstall: Settings → About → "Uninstall PDM…", or Windows "Apps & features". Your downloaded files are kept.

## Updates

Every change merged into `main` is built and tested on Windows by GitHub Actions and published under **Releases**
(the last 5 are kept). PDM checks a few minutes after it starts and every 6 hours; it downloads a newer version,
verifies its SHA-256 and swaps itself in **only while nothing is downloading**. No account or token is needed.
Check by hand: Settings → Updates → **Check now**.

When PDM updates, it also updates the extension folder; the extension notices and reloads itself.

## Browser extension (Opera, Chrome, Edge — Firefox below)

On first start PDM puts the extension in `%LOCALAPPDATA%\PDM\extension` (Settings → Browser → "Open extension folder").
The same extension works in Opera, Chrome and Edge; you can load it into several browsers from the same folder
(PDM → Settings → Browser → "How to install the browser extension?" has a button for each browser).

1. Open the extensions page: Opera `opera://extensions`, Chrome `chrome://extensions`, Edge `edge://extensions`.
2. Turn on **Developer mode** (Opera and Chrome: top right; Edge: on the left).
3. Click **Load unpacked**.
4. Choose `%LOCALAPPDATA%\PDM\extension` (or `dist\extension\opera`).
5. "PDM Download Manager" appears. Its ID must be **`dloanbeiidcfnnmdmgpcdhbkenjjangb`** — PDM accepts no other.
6. Click the extension icon: it should say "PDM connected".

> Browsers may show a notice about developer-mode extensions at start-up; this is expected.

### Firefox

The Firefox add-on has the same features. PDM puts it in `%LOCALAPPDATA%\PDM\firefox\pdm-firefox.xpi`.

- **Install:** PDM → Settings → Browser → "How to install the browser extension?" → **Add to Firefox**.
- In Firefox a download starts in Firefox first; the add-on pauses it at once and asks PDM. If PDM takes it, Firefox's
  copy is cancelled; otherwise it continues in Firefox (nothing is lost).

#### Signing the Firefox add-on

Release Firefox only keeps add-ons **signed by Mozilla**. Signing is free and the add-on is not published ("unlisted"):

1. Sign in at https://addons.mozilla.org/developers/addon/api/key/ and click **Generate new credentials**
   (*JWT issuer* and *JWT secret*).
2. In the GitHub repository: **Settings → Secrets and variables → Actions → New repository secret**, add
   `AMO_JWT_ISSUER` and `AMO_JWT_SECRET`.
3. The next build on `main` signs the add-on and puts it inside PDM.

Without a signature "Add to Firefox" opens `about:debugging`: use **Load Temporary Add-on…** and pick `pdm-firefox.xpi`
(it is removed when Firefox closes).

## Usage

- **Downloads from the browser:** for the listed file types (zip, exe, pdf, mp4, …) the download goes to PDM and the
  "Download Info" window opens. If PDM cannot be reached, the download **always continues in the browser**.
- **Keep one download in the browser:** extension icon → "Let the browser handle the next download".
- **Context menu:** "Download with PDM" (link, image, video, audio), "Download selected links with PDM",
  "Download all links with PDM…" (filtered list), "Download this page's video with PDM".
- **Video:** hover a video for the "Download with PDM" button. For YouTube and similar sites install the video tools
  (yt-dlp + FFmpeg + Deno) once; PDM downloads them from their official GitHub releases and verifies them with SHA-256.
  Only for content you are allowed to download; DRM-protected content is not supported.
- **Shortcuts:** Ctrl+N new download, Ctrl+V add links from the clipboard, Ctrl+F search, Enter open/details, Delete remove.
- **Queues and scheduler:** "Download Later" puts downloads in a queue (right click → "Add to queue" to move them).
  The scheduler window creates queues and sets start/stop times, days, files at the same time and what to do at the end.
- **Expired link (403/410):** right click → "Refresh address…" and paste the new link; progress is kept.
- **Folders by type:** downloads go to subfolders of your Downloads folder — **Compressed**, **Documents**, **Music**,
  **Programs**, **Video**; other files go to Downloads itself. Turn off in Settings → Files.

## Security

- PDM **opens no network ports**. The extension talks to it through a local named pipe open only to your Windows user,
  and only the PDM extension's ID is accepted.
- Only `http`/`https` is downloaded; certificate checks cannot be turned off.
- File names are cleaned (writing outside the folder, hidden Unicode tricks and reserved names are blocked).
- Finished files are handed to Windows ("downloaded from the internet" mark + antivirus scan). Downloads are never run
  automatically; PDM warns before opening program files (.exe, .msi, .bat, .ps1, .js, .vbs, .lnk, .url, .chm, .msc,
  .library-ms, .xll, .appinstaller and similar).
- Browser cookies are stored encrypted with Windows DPAPI and deleted when the download finishes. Logs never contain
  cookies, headers, query strings or passwords.
- Site and proxy passwords are DPAPI-encrypted. A site password is only sent to that site, when it asks; "Basic" logins,
  which send the password readable, are only used over https.
- Deno (official release, SHA-256 verified) runs only under yt-dlp, sandboxed: no file, network or environment access.
- Archives are extracted only into a new folder next to them (entries that would write elsewhere are rejected).
- No administrator rights; everything is per user (HKCU).
- DLL planting protection: PDM loads Windows DLLs from System32 only; the current folder, network shares and
  low-integrity files are removed from the DLL search.
- Helper tools run in a Windows job object: FFmpeg and tar (which read untrusted files) cannot start other programs;
  no tool can touch the clipboard, desktop or system settings; they all end when PDM ends. This limits what a
  compromised tool can start; it is not a file-system or network sandbox, and PDM does not claim one.
- Updates come only from this repository's GitHub releases, over HTTPS, verified with SHA-256, and only to a newer version.
- The CI build runs with read-only permissions; only a small separate release step can publish.

Found a security problem? Please report it privately through the repository's **Security** tab
("Report a vulnerability") instead of opening a public issue.

## Building

On Windows, in the repository folder:

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

Details and requirements: [BUILD.md](BUILD.md). Output:

- `dist\PDM-Setup.exe` — installer
- `dist\PDM.exe` — the app (also runs without installing)
- `dist\extension\opera` — unpacked Opera / Chrome / Edge extension
- `dist\extension\firefox\pdm-firefox.xpi` — Firefox add-on

Every push also builds on GitHub Actions; the build is kept for 30 days under **Actions → build → Artifacts**.

## Files

| Location | Contents |
| --- | --- |
| `%LOCALAPPDATA%\Programs\PDM\PDM.exe` | Installed app |
| `%LOCALAPPDATA%\PDM\data` | Settings and download list |
| `%LOCALAPPDATA%\PDM\logs` | Logs (kept 7 days) |
| `%LOCALAPPDATA%\PDM\host` | Browser bridge (native messaging host) |
| `%LOCALAPPDATA%\PDM\extension` | Opera / Chrome / Edge extension |
| `%LOCALAPPDATA%\PDM\firefox` | Firefox add-on (.xpi) |
| `%LOCALAPPDATA%\PDM\tools` | Optional yt-dlp / FFmpeg / Deno |

## License

[MIT](LICENSE). The optional video tools are separate programs with their own licenses and are downloaded from their
official releases only if you install them: yt-dlp (Unlicense) and Deno (MIT) from their own projects, and the Windows
FFmpeg (GPL) build from the [yt-dlp/FFmpeg-Builds](https://github.com/yt-dlp/FFmpeg-Builds) project (not from the FFmpeg
project itself). Each download is checked against the SHA-256 list published in the same release, which proves the file
is the one that release contains, not who built it.

PDM is an independent project and is not affiliated with any other download manager.
You are responsible for having the right to download the content you download with PDM.
