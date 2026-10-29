using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PDM.Core;
using PDM.Core.Engine;
using PDM.Core.Ipc;
using PDM.Core.Models;
using PDM.Core.Security;
using PDM.Core.Video;

namespace PDM.Tests;

internal static class Program
{
    private static int _pass, _fail;
    private static readonly List<string> Report = new();
    private static string _work = "";
    private static TestServer _srv = null!;

    private static async Task<int> Main(string[] args)
    {
        // The whole suite runs under the app's process protections, so anything they would break shows up here.
        Hardening.Apply();
        _work = Path.Combine(Path.GetTempPath(), "pdm-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_work);
        AppPaths.DataRootOverride = Path.Combine(_work, "appdata");
        AppPaths.EnsureDirectories();
        Log.Init(AppPaths.LogDir);
        _srv = new TestServer();
        var hostExe = args.FirstOrDefault(a => a.EndsWith("PDM.NativeHost.exe", StringComparison.OrdinalIgnoreCase));

        var sw = Stopwatch.StartNew();
        // ---- unit ----
        Run("FileNames: path traversal, reserved names, bidi, length", TestSanitize);
        Run("FileNames: Content-Disposition (filename*, UTF-8, quotes)", TestContentDisposition);
        Run("FileNames: CombineInside rejects escape", TestCombineInside);
        Run("FileNames: MakeUnique", TestMakeUnique);
        Run("DPAPI round trip", TestDpapi);
        Run("Bridge: rejects unknown origin", TestBridgeOrigin);
        Run("Bridge: only http(s) links, caps sizes", TestBridgeValidation);
        Run("Bridge: video message", TestBridgeVideo);
        Run("Firefox: bridge accepts our add-on ID, update request; host manifests (Chromium only chrome-extension://, Firefox allowed_extensions)", TestFirefoxBridge);
        Run("Firefox: add-on manifest matches the Chromium one (ID, permissions, scripts, event page)", TestFirefoxManifestFile);
        Run("Languages: app (en, tr, de, fr, it) and extension have every string with the same placeholders", TestTranslations);
        Run("yt-dlp: arguments are safe (--ignore-config, '--' before URL)", TestYtDlpArgs);
        Run("yt-dlp: mp4 prefers H.264/AAC and avoids AV1", TestYtDlpFormat);
        Run("Video: the same video added twice gets two temp folders (no shared parts, no deleting each other's)", TestVideoTempPerJob);
        Run("yt-dlp: info JSON parsing", TestYtDlpInfo);
        Run("ToolManager: checksum list parsing", TestFindHash);
        Run("Cookies: container domain rules", TestCookieContainer);
        Run("Cookies: Netscape cookies.txt for yt-dlp", TestNetscapeCookies);
        Run("Updater: release parsing, versions, checksum", TestUpdater);
        Run("Video: direct-stream plan from yt-dlp JSON (multi-connection path)", TestDirectPlan);
        Run("Batch pattern: [001-003], [a-c], several ranges, descending, cap", TestBatchPattern);
        Run("Auto retry: only transient errors are retried", TestTransientErrors);
        Run("Disk space: free bytes, room check, disk-full error code", TestDiskSpace);
        Run("Security: more dangerous types warned, addresses in tool errors redacted, DLL load policy on", TestSecurityHelpers);
        Run("Settings: old file (no version) → category folders on, scheduler moved into the main queue", TestSettingsMigration);
        Run("Queues: old 'inQueue' lists load into the main queue; queue ids round-trip", TestQueueIds);
        Run("Archives: zip extracts into a new folder; '..' entry refused and nothing left behind", TestZipExtract);
        Run("History: totals per day, survives reload, clear", TestHistory);
        Run("Export/import: .json round trip, links found in text, only http(s), no duplicates", TestListTransfer);
        Run("Proxy: address parsing (host:port, http, socks5; bad ones refused)", TestProxyParse);
        Run("Grabber: links from HTML (relative, <base>, entities, no javascript:/#)", TestExtractLinks);
        Run("Convert: FFmpeg stream info and codec choice (copy what fits, H.264/AAC otherwise)", TestConvertPlan);

        // ---- engine (local HTTP server) ----
        await RunAsync("1 Parallel download 24 MB, 8 connections, SHA-256", TestParallel);
        await RunAsync("2 Server without Range → single connection fallback", TestNoRange);
        await RunAsync("2b Bounded requests (1 MB pieces, YouTube style) → OK, hash OK", TestBoundedRequests);
        await RunAsync("2c Bounded requests + connection drops → retries, hash OK", TestBoundedWithDrops);
        await RunAsync("3 Pause + resume (new engine instance) continues, hash OK", TestPauseResume);
        await RunAsync("3b Resume after server file changed → restarts from zero", TestChangedOnResume);
        await RunAsync("3c Resume when the server dropped its ETag (same size, new bytes) → restarts, no mix", TestEtagGoneOnResume);
        await RunAsync("7a Connection drops → retries, hash OK", TestDrops);
        await RunAsync("7b 429 + Retry-After → waits and succeeds", Test429);
        await RunAsync("7c 404 → fails with HTTP 404", Test404);
        await RunAsync("7d 403 → fails with HTTP 403", Test403);
        await RunAsync("Unknown size (chunked) → OK", TestChunked);
        await RunAsync("Redirect → follows, uses final URL", TestRedirect);
        await RunAsync("Cookies sent only to their own host across redirects", TestCookiesScoped);
        await RunAsync("Speed limit 1 MB/s is respected", TestSpeedLimit);
        await RunAsync("Speed limits: a download's own low limit does not use up the shared limit of the others", TestSharedLimitNotWasted);
        await RunAsync("Probe: name from Content-Disposition (UTF-8), size", TestProbe);
        await RunAsync("Manager: add → complete → renamed, Mark-of-the-Web, saved list", TestManagerEndToEnd);
        await RunAsync("Manager: expected SHA-256 mismatch → error", TestManagerHashMismatch);
        await RunAsync("Manager: cancel/remove deletes partial file", TestManagerRemove);
        await RunAsync("Queues: a queue with 1-at-a-time runs its downloads one after another, then reports finished", TestQueueOneAtATime);
        await RunAsync("Queues: stopping a queue is not \"finished\" (no sleep/shutdown), and no auto retry restarts it", TestQueueStopIsNotFinished);
        await RunAsync("Site grabber: a page whose body stops is given up after the stall limit, not held for the whole timeout", TestGrabberBodyStall);
        Run("Review follow-ups: huge RAR part number, raw cookie values", TestReviewSmallFixes);
        await RunAsync("Engine: a server that never sends response headers is given up after the header timeout", TestHeaderTimeout);
        await RunAsync("Browser hand-over: the download is saved to disk before PDM answers ok; not its own duplicate", TestPendingHandOver);
        await RunAsync("Archives: .tar.gz extracts with the system tar", TestTarExtract);
        await RunAsync("Proxy: downloads go through the custom proxy (and its login)", TestProxyRouting);
        await RunAsync("Site login: 401 without login; Basic login only for its own host", TestSiteLogin);
        await RunAsync("Grabber: depth, same site, file types, file behind a page-like link", TestGrabber);
        await RunAsync("Convert: webm (VP9/Opus) → MP4 (H.264/AAC) and → MP3 with FFmpeg", TestConvert);
        await RunAsync("yt-dlp cookies: YouTube without cookies first; login sites with them; the other choice on failure", TestYtDlpCookieChoice);
        await RunAsync("Tool job: no child processes, limit reads back, closing it ends the tool", TestToolJob);
        await RunAsync("IPC: named pipe round trip (current-user only)", TestPipe);
        if (hostExe != null && File.Exists(hostExe))
        {
            await RunAsync("4 Native host exe: extension message → pipe → reply", () => TestNativeHost(hostExe));
            await RunAsync("4b Native host exe: rejects bad origin argument", () => TestNativeHostBadOrigin(hostExe));
            await RunAsync("4c Native host exe: Firefox (manifest path + our add-on ID) accepted, other add-ons refused", () => TestNativeHostFirefox(hostExe));
        }
        else
        {
            Skip("Native host exe tests", "pass the path to PDM.NativeHost.exe as an argument");
        }

        _srv.Dispose();
        try { Directory.Delete(_work, true); } catch { }
        Console.WriteLine();
        foreach (var r in Report) Console.WriteLine(r);
        Console.WriteLine($"\n{_pass} passed, {_fail} failed, {_skip} skipped (not run here) in {sw.Elapsed.TotalSeconds:F1}s");
        return _fail == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ harness

    private static void Run(string name, Action test)
    {
        try { test(); Pass(name); }
        catch (SkippedException skip) { Skip(name, skip.Message); }
        catch (Exception ex) { Fail(name, ex); }
    }

    private static async Task RunAsync(string name, Func<Task> test)
    {
        _srv.ResetCounters();
        var sw = Stopwatch.StartNew();
        try
        {
            await test().WaitAsync(TimeSpan.FromMinutes(3));
            Pass($"{name} ({sw.Elapsed.TotalSeconds:F1}s)");
        }
        catch (SkippedException skip) { Skip(name, skip.Message); }
        catch (Exception ex) { Fail(name, ex); }
    }

    /// <summary>A test that could not run here (needs Windows, or a tool that is not installed). Counted apart: it is
    /// not a pass, and what it would have checked is still unchecked.</summary>
    private sealed class SkippedException(string reason) : Exception(reason);
    private static int _skip;

    private static void Pass(string name) { _pass++; Report.Add("PASS  " + name); Console.Write('.'); }
    private static void Skip(string name, string reason) { _skip++; Report.Add($"SKIP  {name}\n      {reason}"); Console.Write('s'); }
    private static void Fail(string name, Exception ex) { _fail++; Report.Add($"FAIL  {name}\n      {ex.GetType().Name}: {ex.Message}"); Console.Write('F'); }

    private static void Assert(bool cond, string message) { if (!cond) throw new Exception(message); }
    private static void Eq<T>(T expected, T actual, string what) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: expected '{expected}', got '{actual}'"); }

    // ------------------------------------------------------------------ unit tests

    private static void TestSanitize()
    {
        Eq("passwd", FileNames.Sanitize("../../etc/passwd"), "traversal");
        Eq("evil.exe", FileNames.Sanitize("C:\\Windows\\evil.exe"), "abs path");
        Eq("_CON.txt", FileNames.Sanitize("CON.txt"), "reserved");
        Eq("_nul", FileNames.Sanitize("nul"), "reserved lower");
        Eq("invoicefdp.exe", FileNames.Sanitize("invoice\u202Efdp.exe"), "bidi override removed");
        Eq("a_b_c_.txt", FileNames.Sanitize("a<b>c?.txt"), "invalid chars");
        Eq("file", FileNames.Sanitize("file. . ."), "trailing dots");
        Eq("hidden", FileNames.Sanitize(".hidden"), "leading dot");
        Eq("download", FileNames.Sanitize("   "), "empty");
        Eq("download", FileNames.Sanitize(".."), "dotdot");
        var longName = new string('a', 400) + ".zip";
        var s = FileNames.Sanitize(longName);
        Assert(s.Length <= FileNames.MaxLength && s.EndsWith(".zip"), "long name keeps extension");
        Eq("café résumé.pdf", FileNames.Sanitize("café résumé.pdf"), "unicode kept");
    }

    private static void TestContentDisposition()
    {
        Eq("über straße.bin", FileNames.FromContentDisposition("attachment; filename=\"x.bin\"; filename*=UTF-8''%C3%BCber%20stra%C3%9Fe.bin"), "filename*");
        Eq("report.pdf", FileNames.FromContentDisposition("attachment; filename=\"report.pdf\""), "quoted");
        Eq("my file.zip", FileNames.FromContentDisposition("attachment; filename=my%20file.zip"), "percent");
        var raw = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes("déjà vu.docx"));
        Eq("déjà vu.docx", FileNames.FromContentDisposition($"attachment; filename=\"{raw}\""), "raw utf8");
        Eq(null, FileNames.FromContentDisposition("inline"), "none");
    }

    private static void TestCombineInside()
    {
        var root = Path.Combine(_work, "root");
        Assert(FileNames.CombineInside(root, "a.txt").StartsWith(root), "inside");
        var threw = false;
        try { FileNames.CombineInside(root, "..\\x.txt"); } catch (InvalidOperationException) { threw = true; }
        Assert(threw, "escape must throw");
    }

    private static void TestMakeUnique()
    {
        var dir = Path.Combine(_work, "uniq");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.zip"), "x");
        File.WriteAllText(Path.Combine(dir, "a (1).zip.pdmdl"), "x");
        Eq("a (2).zip", FileNames.MakeUnique(dir, "a.zip"), "unique");
        Eq("b.zip", FileNames.MakeUnique(dir, "b.zip"), "free");
    }

    private static void TestDpapi()
    {
        var p = Dpapi.Protect("session=secret");
        Assert(!p.Contains("secret"), "ciphertext must not contain plaintext");
        Eq("session=secret", Dpapi.Unprotect(p), "round trip");
        Eq(null, Dpapi.Unprotect("bm90LWRwYXBp"), "garbage → null");
    }

    private static readonly string[] Allowed = [ExtensionInfo.Origin];

    private static byte[] Wrap(string origin, string msg) => Encoding.UTF8.GetBytes($"{{\"origin\":\"{origin}\",\"msg\":{msg}}}");

    private static void TestBridgeOrigin()
    {
        var threw = false;
        try { BridgeProtocol.Parse(Wrap("chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/", "{\"type\":\"ping\"}"), Allowed); }
        catch (BridgeException e) { threw = e.Code == "origin"; }
        Assert(threw, "foreign origin must be rejected");
        Assert(BridgeProtocol.Parse(Wrap(ExtensionInfo.Origin, "{\"type\":\"ping\"}"), Allowed) is PingMessage, "own origin ok");
    }

    private static void TestFirefoxBridge()
    {
        string[] allowed = [ExtensionInfo.Origin, ExtensionInfo.FirefoxId];
        Assert(BridgeProtocol.Parse(Wrap(ExtensionInfo.FirefoxId, "{\"type\":\"ping\"}"), allowed) is PingMessage, "Firefox add-on accepted");
        var threw = false;
        try { BridgeProtocol.Parse(Wrap("someone-else@example.com", "{\"type\":\"ping\"}"), allowed); }
        catch (BridgeException e) { threw = e.Code == "origin"; }
        Assert(threw, "another Firefox add-on is rejected");
        Assert(BridgeProtocol.Parse(Wrap(ExtensionInfo.FirefoxId, "{\"type\":\"updateExtension\"}"), allowed) is UpdateExtensionMessage { Firefox: true }, "update request from Firefox");
        Assert(BridgeProtocol.Parse(Wrap(ExtensionInfo.Origin, "{\"type\":\"updateExtension\"}"), allowed) is UpdateExtensionMessage { Firefox: false }, "update request from Chromium");

        using var chromium = JsonDocument.Parse(NativeHostRegistration.ChromiumManifest(allowed, @"C:\h\PDM.NativeHost.exe"));
        var origins = chromium.RootElement.GetProperty("allowed_origins").EnumerateArray().Select(e => e.GetString()).ToList();
        Eq(ExtensionInfo.Origin, string.Join("|", origins), "Chromium manifest lists only chrome-extension:// origins");
        Assert(!chromium.RootElement.TryGetProperty("allowed_extensions", out _), "no Firefox key in the Chromium manifest");
        using var ff = JsonDocument.Parse(NativeHostRegistration.FirefoxManifest(@"C:\h\PDM.NativeHost.exe"));
        Eq(ExtensionInfo.FirefoxId, ff.RootElement.GetProperty("allowed_extensions")[0].GetString(), "Firefox allowed_extensions");
        Eq(1, ff.RootElement.GetProperty("allowed_extensions").GetArrayLength(), "only our add-on");
        Assert(!ff.RootElement.TryGetProperty("allowed_origins", out _), "no allowed_origins in the Firefox manifest");
        Eq("stdio", ff.RootElement.GetProperty("type").GetString(), "type");
        Eq(AppPaths.NativeHostName, ff.RootElement.GetProperty("name").GetString(), "name");
    }

    /// <summary>The repository folder (tests run from bin\…); null when the sources are not around.</summary>
    private static string? RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "extension", "firefox", "manifest.json"))) return d.FullName;
        return null;
    }

    private static void TestTranslations()
    {
        var root = RepoRoot() ?? throw new Exception("repository not found");
        string Src(string file) => File.ReadAllText(Path.Combine(root, "src", "PDM", file));
        const string lit = "\"((?:[^\"\\\\]|\\\\.)*)\"";
        static string Holes(string text) => string.Join(",", Regex.Matches(text, @"\{\d[^}]*\}").Select(m => m.Value).Order(StringComparer.Ordinal));
        // Loc.cs: ["Key"] = ("Turkish", "English")
        var main = Regex.Matches(Src("Loc.cs"), @"\[""(\w+)""\]\s*=\s*\(\s*" + lit + @"\s*,\s*" + lit + @"\s*\)", RegexOptions.Singleline)
            .ToDictionary(m => m.Groups[1].Value, m => (Tr: m.Groups[2].Value, En: m.Groups[3].Value));
        Assert(main.Count > 300, "Loc.cs strings found: " + main.Count);
        foreach (var (k, v) in main) Eq(Holes(v.En), Holes(v.Tr), "tr placeholders " + k);
        foreach (var file in new[] { "LocDe.cs", "LocFr.cs", "LocIt.cs" })
        {
            var other = Regex.Matches(Src(file), @"\[""(\w+)""\]\s*=\s*" + lit + ",").ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
            Eq("", string.Join(",", main.Keys.Except(other.Keys)), file + " missing keys");
            Eq("", string.Join(",", other.Keys.Except(main.Keys)), file + " unknown keys");
            foreach (var (k, v) in other) Eq(Holes(main[k].En), Holes(v), file + " placeholders " + k);
        }
        // Extension: every _locales/<lang>/messages.json has the English keys and the same $A$/$B$.
        var dir = Path.Combine(root, "extension", "opera", "_locales");
        using var enDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "en", "messages.json")));
        foreach (var lang in new[] { "tr", "de", "fr", "it" })
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, lang, "messages.json")));
            foreach (var e in enDoc.RootElement.EnumerateObject())
            {
                Assert(doc.RootElement.TryGetProperty(e.Name, out var t), $"{lang}: {e.Name} missing");
                var en = e.Value.GetProperty("message").GetString()!;
                var tr = t.GetProperty("message").GetString()!;
                foreach (var ph in new[] { "$A$", "$B$" }) Eq(en.Contains(ph), tr.Contains(ph), $"{lang}: {e.Name} {ph}");
            }
        }
    }

    private static void TestVideoTempPerJob()
    {
        var o = new VideoOptions { Format = YtDlpRunner.FormatFor(1080) };
        const string url = "https://www.youtube.com/watch?v=aqz-KE-bpKQ";
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        Assert(YtDlpRunner.JobTempDir(url, o, a) != YtDlpRunner.JobTempDir(url, o, b), "two jobs, two folders");
        Eq(YtDlpRunner.JobTempDir(url, o, a), YtDlpRunner.JobTempDir(url, o, a), "stable for one job (resume)");
        // An old shared folder (before job ids) is taken over by exactly one job; the other starts in its own folder.
        var legacy = YtDlpRunner.LegacyJobTempDir(url, o);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "pdm-state.json"), "{}");
        YtDlpRunner.AdoptLegacyTempDir(url, o, a);
        YtDlpRunner.AdoptLegacyTempDir(url, o, b);
        Assert(!Directory.Exists(legacy), "old folder taken over");
        Assert(File.Exists(Path.Combine(YtDlpRunner.JobTempDir(url, o, a), "pdm-state.json")), "first job keeps the progress");
        Assert(!Directory.Exists(YtDlpRunner.JobTempDir(url, o, b)), "second job does not share it");
        Directory.Delete(YtDlpRunner.JobTempDir(url, o, a), true);
    }

    private static void TestFirefoxManifestFile()
    {
        var root = RepoRoot() ?? throw new Exception("repository not found");
        using var ff = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "extension", "firefox", "manifest.json")));
        using var cr = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "extension", "opera", "manifest.json")));
        var f = ff.RootElement;
        var c = cr.RootElement;
        Eq(ExtensionInfo.FirefoxId, f.GetProperty("browser_specific_settings").GetProperty("gecko").GetProperty("id").GetString(), "gecko id = host's Firefox ID");
        Eq("background.js", f.GetProperty("background").GetProperty("scripts")[0].GetString(), "event page script");
        Assert(!f.GetProperty("background").TryGetProperty("service_worker", out _), "no service worker (Firefox)");
        Assert(!f.TryGetProperty("key", out _), "no Chromium key");
        string Join(JsonElement e, string name) => string.Join(",", e.GetProperty(name).EnumerateArray().Select(x => x.GetString()).Order(StringComparer.Ordinal));
        Eq(Join(c, "permissions"), Join(f, "permissions"), "same permissions");
        Eq(Join(c, "host_permissions"), Join(f, "host_permissions"), "same host permissions");
        Eq(c.GetProperty("content_scripts").GetRawText(), f.GetProperty("content_scripts").GetRawText(), "same content scripts");
        Eq(c.GetProperty("content_security_policy").GetRawText(), f.GetProperty("content_security_policy").GetRawText(), "same CSP");
        Eq(c.GetProperty("version").GetString(), f.GetProperty("version").GetString(), "same base version");
    }

    private static void TestBridgeValidation()
    {
        var msg = """
        {"type":"download","interactive":true,"items":[
          {"url":"javascript:alert(1)"},
          {"url":"file:///C:/Windows/win.ini"},
          {"url":"https://example.com/a.zip","filename":"..\\..\\x.zip","size":123,"referrer":"javascript:x",
           "cookies":[{"name":"s","value":"v","domain":"example.com","hostOnly":true,"secure":true},{"name":"","domain":"x"}]},
          {"url":"http://example.org/b.iso"}
        ]}
        """;
        var m = (DownloadMessage)BridgeProtocol.Parse(Wrap(ExtensionInfo.Origin, msg), Allowed);
        Eq(2, m.Items.Count, "only http(s) items");
        Eq("https://example.com/a.zip", m.Items[0].Url, "url");
        Eq(null, m.Items[0].Referrer, "javascript referrer dropped");
        Eq(1, m.Items[0].Cookies!.Count, "invalid cookie dropped");
        Eq(123L, m.Items[0].SizeHint, "size");
        Assert(m.Interactive, "interactive");
        var threw = false;
        try { BridgeProtocol.Parse(Wrap(ExtensionInfo.Origin, "{\"type\":\"download\",\"items\":[{\"url\":\"ftp://x/y\"}]}"), Allowed); }
        catch (BridgeException) { threw = true; }
        Assert(threw, "no valid items → error");
        threw = false;
        try { BridgeProtocol.Parse(Encoding.UTF8.GetBytes("{not json"), Allowed); } catch (BridgeException) { threw = true; }
        Assert(threw, "bad json");
    }

    private static void TestBridgeVideo()
    {
        var v = (VideoMessage)BridgeProtocol.Parse(Wrap(ExtensionInfo.Origin,
            "{\"type\":\"video\",\"pageUrl\":\"https://www.youtube.com/watch?v=abc\",\"title\":\"T\",\"media\":[{\"url\":\"https://cdn/x.mp4\",\"mime\":\"video/mp4\",\"size\":10},{\"url\":\"data:x\"}]}"), Allowed);
        Eq("T", v.Title, "title");
        Eq(1, v.Media.Count, "media filtered");
        Assert(v.Referrer == null, "no referrer");
        var embed = (VideoMessage)BridgeProtocol.Parse(Wrap(ExtensionInfo.Origin,
            "{\"type\":\"video\",\"pageUrl\":\"https://player.example/embed/1\",\"referrer\":\"https://blog.example/post\"}"), Allowed);
        Eq("https://blog.example/post", embed.Referrer, "referrer of an embedded player");
        var bad = (VideoMessage)BridgeProtocol.Parse(Wrap(ExtensionInfo.Origin,
            "{\"type\":\"video\",\"pageUrl\":\"https://player.example/embed/1\",\"referrer\":\"javascript:alert(1)\"}"), Allowed);
        Assert(bad.Referrer == null, "non-http referrer dropped");
    }

    private static void TestNetscapeCookies()
    {
        var text = Cookies.ToNetscape([
            new CookieData { Name = "sid", Value = "v1", Domain = "www.instagram.com", Path = "/", HostOnly = true, Secure = true },
            new CookieData { Name = "auth", Value = "v2", Domain = ".x.com", Path = "/" },
            new CookieData { Name = "bad", Value = "a\tb", Domain = "x.com" },
            new CookieData { Name = "evil\nline", Value = "v", Domain = "x.com" },
        ], DateTimeOffset.FromUnixTimeSeconds(1900000000));
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Eq("# Netscape HTTP Cookie File", lines[0], "header");
        Eq("www.instagram.com\tFALSE\t/\tTRUE\t1900000000\tsid\tv1", lines[1], "host-only cookie");
        Eq(".x.com\tTRUE\t/\tFALSE\t1900000000\tauth\tv2", lines[2], "domain cookie");
        Eq(3, lines.Length, "cookies with tabs or line breaks are left out");
    }

    private static void TestDirectPlan()
    {
        const string two = """
            {"_type":"video","title":"T: x","filename":"C:\\Downloads\\T： x 1080p.webm","requested_formats":[
             {"url":"https://rr1.googlevideo.com/videoplayback?a=1","protocol":"https","ext":"mp4","filesize":1000,"vcodec":"avc1.640028","acodec":"none",
              "http_headers":{"User-Agent":"UA/1","Referer":"https://www.youtube.com/"},"downloader_options":{"http_chunk_size":10485760}},
             {"url":"https://rr1.googlevideo.com/videoplayback?a=2","protocol":"https","ext":"m4a","filesize_approx":200,"vcodec":"none","acodec":"mp4a.40.2"}]}
            """;
        var plan = YtDlpRunner.ParseDirectPlan(two, "mp4")!;
        Eq(2, plan.Streams.Count, "two streams");
        Eq("T： x 1080p.mp4", plan.FileName, "file name from yt-dlp, container extension");
        Assert(plan.Streams[0].HasVideo && !plan.Streams[0].HasAudio && plan.Streams[1].HasAudio, "video + audio");
        Eq(10485760L, plan.Streams[0].ChunkSize, "YouTube chunk size kept");
        Eq("UA/1", plan.Streams[0].UserAgent, "headers kept");
        Eq(200L, plan.Streams[1].Size, "approx size used");
        Assert(YtDlpRunner.ParseDirectPlan(two.Replace("\"protocol\":\"https\",\"ext\":\"mp4\"", "\"protocol\":\"m3u8_native\",\"ext\":\"mp4\""), "mp4") == null, "HLS stays with yt-dlp");
        Assert(YtDlpRunner.ParseDirectPlan(two.Replace("https://rr1.googlevideo.com/videoplayback?a=1", "file:///C:/x"), "mp4") == null, "only http(s) URLs");
        Assert(YtDlpRunner.ParseDirectPlan("{\"_type\":\"playlist\",\"entries\":[]}", "mp4") == null, "playlists stay with yt-dlp");
        Assert(YtDlpRunner.ParseDirectPlan("{\"is_live\":true,\"url\":\"https://x/y.mp4\",\"protocol\":\"https\"}", "mp4") == null, "live streams stay with yt-dlp");
        var single = YtDlpRunner.ParseDirectPlan("{\"title\":\"Clip\",\"url\":\"https://cdn.example/clip.mp4\",\"protocol\":\"https\",\"ext\":\"mp4\",\"vcodec\":\"avc1\",\"acodec\":\"mp4a\"}", "mkv")!;
        Eq(1, single.Streams.Count, "progressive: one stream");
        Eq("Clip.mp4", single.FileName, "single stream keeps its own extension");
    }

    private static void TestUpdater()
    {
        Assert(PDM.Core.Update.UpdateClient.IsNewer("1.1.12", "1.0.0"), "1.1.12 > 1.0.0");
        Assert(PDM.Core.Update.UpdateClient.IsNewer("v1.1.12", "1.1.9"), "numeric, not text, comparison");
        Assert(!PDM.Core.Update.UpdateClient.IsNewer("1.1.9", "1.1.12"), "older is not newer");
        Assert(!PDM.Core.Update.UpdateClient.IsNewer("garbage", "1.0.0"), "bad version ignored");
        const string api = "https://api.github.com/repos/Maetorn/pdm-download-manager/releases/assets/";
        var json = "{\"tag_name\":\"v1.1.20\",\"draft\":false,\"body\":\"notes\",\"assets\":[" +
                   "{\"name\":\"PDM.exe\",\"url\":\"" + api + "1\",\"size\":75000000}," +
                   "{\"name\":\"PDM.exe.sha256\",\"url\":\"" + api + "2\",\"size\":75}]}";
        var info = PDM.Core.Update.UpdateClient.ParseRelease(json)!;
        Eq("1.1.20", info.Version, "version from tag");
        Eq(api + "1", info.ExeAssetUrl, "exe asset");
        Eq(api + "2", info.ShaAssetUrl, "sha asset");
        Eq(75000000L, info.Size, "size");
        var foreign = json.Replace(api + "1", "https://evil.example/PDM.exe");
        Assert(PDM.Core.Update.UpdateClient.ParseRelease(foreign) == null, "asset outside this repository's API is refused");
        Assert(PDM.Core.Update.UpdateClient.ParseRelease(json.Replace("\"draft\":false", "\"draft\":true")) == null, "drafts ignored");
        Assert(PDM.Core.Update.UpdateClient.ParseRelease("{\"tag_name\":\"v1.1.20\",\"assets\":[]}") == null, "no assets → no update");
        var hash = new string('a', 64);
        Eq(hash, PDM.Core.Update.UpdateClient.ParseSha256(hash.ToUpperInvariant() + "  PDM.exe\n"), "sha256sum format");
        Assert(PDM.Core.Update.UpdateClient.ParseSha256("not a hash") == null, "bad checksum refused");
        Assert(!string.IsNullOrEmpty(AppPaths.Version) && Version.TryParse(AppPaths.Version, out _), "AppPaths.Version is a version: " + AppPaths.Version);
    }

    private static void TestYtDlpFormat()
    {
        var mp4 = YtDlpRunner.FormatFor(1080, "mp4").Split('/');
        Assert(mp4[0].Contains("[height<=1080]") && mp4[0].Contains("[vcodec^=avc]") && mp4[0].Contains("[acodec^=mp4a]"), "first choice H.264 + AAC: " + mp4[0]);
        Assert(mp4.Take(mp4.Length - 3).All(p => !p.Contains("av01") || p.Contains("!^=av01")), "AV1 only as a last resort");
        var uhd = YtDlpRunner.FormatFor(2160, "mp4");
        Assert(!uhd.Contains("vcodec^=avc"), "above 1080p no H.264 restriction (sites only have VP9/AV1 there)");
        Assert(uhd.StartsWith("bv*[height<=2160][vcodec!^=av01]"), "4K: non-AV1 first: " + uhd);
        Eq("bv*[height<=720]+ba/b[height<=720]/bv*+ba/b", YtDlpRunner.FormatFor(720, "mkv"), "mkv takes the best streams");
        Assert(YtDlpRunner.FormatFor(null, "mp4").EndsWith("bv*+ba/b"), "always ends with a catch-all");
    }

    private static void TestYtDlpArgs()
    {
        var tools = new ToolManager(Path.Combine(_work, "tools"));
        var r = new YtDlpRunner(tools, "https://www.youtube.com/watch?v=x&list=y", "C:\\Downloads", new VideoOptions { Format = YtDlpRunner.FormatFor(1080) }, "https://www.youtube.com/");
        var a = r.BuildArguments();
        Eq("--ignore-config", a[0], "ignore config first");
        Eq("--", a[^2], "-- before URL");
        Eq("https://www.youtube.com/watch?v=x&list=y", a[^1], "url last");
        Assert(a.Contains("--no-playlist"), "no playlist by default");
        Eq(YtDlpRunner.FormatFor(1080), a[a.IndexOf("-f") + 1], "format passed after -f");
        Assert(!a.Contains("--exec"), "no exec");
        // --print would make yt-dlp quiet (no progress); the final path goes to a file instead.
        Assert(!a.Contains("--print") && a.Contains("--print-to-file"), "path via --print-to-file, progress stays on");
        Assert(a[a.IndexOf("-o") + 1].Contains("%(height& {}p|)s"), "quality in the file name, so two qualities do not collide");
        var audio = new YtDlpRunner(tools, "https://www.youtube.com/watch?v=x", "C:\\Downloads", new VideoOptions { AudioOnly = true }, null).BuildArguments();
        Assert(!audio[audio.IndexOf("-o") + 1].Contains("height"), "no quality suffix for audio");
        var other = new YtDlpRunner(tools, "https://www.youtube.com/watch?v=x", "C:\\Downloads", new VideoOptions { Format = YtDlpRunner.FormatFor(720) }, null).BuildArguments();
        Assert(a[a.IndexOf("--paths") + 1] != other[other.IndexOf("--paths") + 1], "each quality has its own temp folder");
        Assert(!a.Contains("--js-runtimes"), "no Deno installed → yt-dlp's own default");

        // With Deno installed it is handed to yt-dlp by full path, before the "--" that ends the options.
        var withDenoDir = Path.Combine(_work, "tools-deno");
        Directory.CreateDirectory(withDenoDir);
        File.WriteAllText(Path.Combine(withDenoDir, "deno.exe"), "");
        var d = new YtDlpRunner(new ToolManager(withDenoDir), "https://www.youtube.com/watch?v=x", "C:\\Downloads", new VideoOptions(), null).BuildArguments();
        var i = d.IndexOf("--js-runtimes");
        Assert(i > 0 && i < d.IndexOf("--"), "js runtime before --");
        Eq("deno:" + Path.Combine(withDenoDir, "deno.exe"), d[i + 1], "deno path");
    }

    private static void TestYtDlpInfo()
    {
        var json = """
        {"title":"Test video","duration":61.5,"extractor_key":"Youtube","formats":[
          {"format_id":"140","vcodec":"none","acodec":"mp4a.40.2","filesize":1000},
          {"format_id":"137","vcodec":"avc1","acodec":"none","height":1080,"filesize":50000},
          {"format_id":"136","vcodec":"avc1","acodec":"none","height":720,"filesize_approx":20000.5},
          {"format_id":"313","vcodec":"vp9","acodec":"none","height":2160},
          {"format_id":"sb0","vcodec":"none","acodec":"none"}]}
        """;
        var info = YtDlpRunner.ParseInfo(json);
        Eq("Test video", info.Title, "title");
        Eq("2160,1080,720", string.Join(",", info.Heights), "heights desc");
        Eq(51000L, info.ApproxSizes[1080], "size incl. audio");
        Assert(info.HasAudio, "audio");
    }

    private static void TestBatchPattern()
    {
        Assert(!BatchPattern.HasPattern("https://a/b.zip"), "no pattern");
        Assert(BatchPattern.HasPattern("https://a/[1-3].zip"), "has pattern");
        Eq("https://a/img001.jpg|https://a/img002.jpg|https://a/img003.jpg",
            string.Join("|", BatchPattern.Expand("https://a/img[001-003].jpg")), "zero padded");
        Eq("x8x|x9x|x10x", string.Join("|", BatchPattern.Expand("x[8-10]x")), "no padding");
        Eq("pa|pb|pc", string.Join("|", BatchPattern.Expand("p[a-c]")), "letters");
        Eq("3|2|1", string.Join("|", BatchPattern.Expand("[3-1]")), "descending");
        Eq("a1|a2|b1|b2", string.Join("|", BatchPattern.Expand("[a-b][1-2]")), "two ranges");
        Eq(5, BatchPattern.Expand("f[1-1000]", max: 5).Count, "capped");
        Eq("[x]", string.Join("|", BatchPattern.Expand("[x]")), "not a range");
    }

    private static void TestTransientErrors()
    {
        foreach (var code in new[] { "network", "http:429", "http:503", "http:504" })
            Assert(DownloadManager.IsTransientError(code), code);
        foreach (var code in new[] { "http:404", "http:403", "disk", "hash", null, "" })
            Assert(!DownloadManager.IsTransientError(code), code ?? "null");
    }

    private static void TestDiskSpace()
    {
        var free = DiskSpace.FreeBytes(_work);
        Assert(free > 0, "free bytes known for temp folder");
        Assert(DiskSpace.HasRoomFor(_work, 1024), "1 KB fits");
        Assert(DiskSpace.HasRoomFor(_work, 0), "unknown size fits");
        Assert(!DiskSpace.HasRoomFor(_work, free), "free space minus reserve does not");
        Eq(0x70, DiskSpace.NotEnoughSpace().HResult & 0xFFFF, "ERROR_DISK_FULL code (engine reports it as disk full)");
    }

    private static void TestSettingsMigration()
    {
        var json = """{"useCategoryFolders": false, "schedulerEnabled": true, "schedulerStartAt": "2030-01-01T02:00:00", "postQueueAction": "Sleep"}""";
        var s = JsonSerializer.Deserialize(json, PdmJson.Default.AppSettings)!;
        s.Normalize();
        Assert(s.UseCategoryFolders, "category folders turned on");
        Eq(QueueInfo.MainId, s.Queues[0].Id, "main queue first");
        Assert(s.Queues[0].ScheduleEnabled, "schedule moved");
        Eq(new DateTime(2030, 1, 1, 2, 0, 0), s.Queues[0].StartAt, "start time moved");
        Eq(PostQueueAction.Sleep, s.Queues[0].PostAction, "post action moved");
        var written = JsonSerializer.Serialize(s, PdmJson.Default.AppSettings);
        Assert(!written.Contains("schedulerEnabled") && !written.Contains("schedulerStartAt") && !written.Contains("postQueueAction"), "old scheduler fields not written again");
        // Once migrated, a user's choice to turn category folders off sticks.
        var again = JsonSerializer.Deserialize(written.Replace("\"useCategoryFolders\": true", "\"useCategoryFolders\": false"), PdmJson.Default.AppSettings)!;
        again.Normalize();
        Assert(!again.UseCategoryFolders, "category folders stay off after migration");
        Eq(1, again.Queues.Count, "no second main queue");
        // A fresh settings object gets the main queue too.
        var fresh = new AppSettings();
        fresh.Normalize();
        Eq(1, fresh.Queues.Count, "fresh: main queue");
        Assert(fresh.Queues[0].RunsOn(DayOfWeek.Sunday), "no days = every day");
    }

    private static void TestQueueIds()
    {
        var old = JsonSerializer.Deserialize("""[{"url":"https://a/1","inQueue":true},{"url":"https://a/2","inQueue":false}]""", PdmJson.Default.ListDownloadItem)!;
        Eq(QueueInfo.MainId, old[0].QueueId, "old inQueue → main");
        Eq(null, old[1].QueueId, "not queued");
        var item = new DownloadItem { Url = "https://a/3", QueueId = "q1" };
        var back = JsonSerializer.Deserialize(JsonSerializer.Serialize(new List<DownloadItem> { item }, PdmJson.Default.ListDownloadItem), PdmJson.Default.ListDownloadItem)!;
        Eq("q1", back[0].QueueId, "queue id round trip");
        Assert(back[0].InQueue, "in a queue");
        back[0].InQueue = false;
        Eq(null, back[0].QueueId, "removed from queue");
    }

    private static void TestZipExtract()
    {
        var dir = Path.Combine(_work, "zip");
        Directory.CreateDirectory(dir);
        var zip = Path.Combine(dir, "paket.zip");
        using (var z = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry("a.txt").Open())) w.Write("hello");
            using (var w = new StreamWriter(z.CreateEntry("sub/b.txt").Open())) w.Write("world");
            z.CreateEntry("empty/");
        }
        Assert(Archives.IsArchive("paket.zip") && Archives.IsArchive("x.tar.gz") && Archives.IsArchive("x.part1.rar") && !Archives.IsArchive("x.part2.rar") && !Archives.IsArchive("x.exe"), "IsArchive");
        var folder = Archives.ExtractAsync(zip).GetAwaiter().GetResult();
        Eq(Path.Combine(dir, "paket"), folder, "target folder");
        Eq("hello", File.ReadAllText(Path.Combine(folder, "a.txt")), "a.txt");
        Eq("world", File.ReadAllText(Path.Combine(folder, "sub", "b.txt")), "sub/b.txt");
        Assert(Directory.Exists(Path.Combine(folder, "empty")), "empty folder");
        Eq(Path.Combine(dir, "paket (1)"), Archives.TargetFolder(zip), "second extraction gets a new folder");

        var evil = Path.Combine(dir, "evil.zip");
        using (var z = System.IO.Compression.ZipFile.Open(evil, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry("ok.txt").Open())) w.Write("ok");
            using (var w = new StreamWriter(z.CreateEntry("../escaped.txt").Open())) w.Write("bad");
        }
        var threw = false;
        try { Archives.ExtractAsync(evil).GetAwaiter().GetResult(); } catch (InvalidDataException) { threw = true; }
        Assert(threw, "'..' entry refused");
        Assert(!File.Exists(Path.Combine(dir, "escaped.txt")) && !File.Exists(Path.Combine(_work, "escaped.txt")), "nothing written outside");
        Assert(!Directory.Exists(Path.Combine(dir, "evil")), "half-made folder removed");
    }

    private static async Task TestTarExtract()
    {
        if (!File.Exists(Archives.TarPath)) { throw new SkippedException("no tar at " + Archives.TarPath); }
        var dir = Path.Combine(_work, "tar");
        var src = Path.Combine(dir, "src");
        Directory.CreateDirectory(Path.Combine(src, "inner"));
        File.WriteAllText(Path.Combine(src, "inner", "c.txt"), "tarred");
        var tgz = Path.Combine(dir, "arsiv.tar.gz");
        using (var fs = File.Create(tgz))
        using (var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Fastest))
            System.Formats.Tar.TarFile.CreateFromDirectory(src, gz, includeBaseDirectory: false);
        var folder = await Archives.ExtractAsync(tgz);
        Eq(Path.Combine(dir, "arsiv"), folder, "target folder (.tar.gz stripped)");
        Eq("tarred", File.ReadAllText(Path.Combine(folder, "inner", "c.txt")), "content");
    }

    private static void TestHistory()
    {
        var h = new History();
        h.Clear(entries: true, stats: true);
        var at = new DateTime(2026, 10, 27, 12, 0, 0);
        h.Record(new DownloadItem { Url = "https://a/1.zip", FileName = "1.zip", Folder = "C:\\d", TotalBytes = 1000, CompletedAt = at });
        h.Record(new DownloadItem { Url = "https://a/2.zip", FileName = "2.zip", Folder = "C:\\d", TotalBytes = 500, CompletedAt = at.AddDays(-1) });
        var snap = h.Snapshot();
        Eq(2, snap.TotalFiles, "files");
        Eq(1500L, snap.TotalBytes, "bytes");
        Eq(1000L, snap.Days["2026-10-27"].Bytes, "per day");
        Eq("2.zip", snap.Entries[0].FileName, "newest first");
        h.Flush();
        var loaded = new History().Snapshot();
        Eq(2, loaded.Entries.Count, "reloaded entries");
        Eq(1500L, loaded.TotalBytes, "reloaded totals");
        h.Clear(entries: true, stats: false);
        Eq(0, h.Snapshot().Entries.Count, "cleared list");
        Eq(1500L, h.Snapshot().TotalBytes, "totals kept");
        h.Flush();
    }

    private static void TestListTransfer()
    {
        var items = new List<DownloadItem>
        {
            new() { Url = "https://a/file.zip", FileName = "file.zip", Referrer = "https://a/" },
            new() { Url = "https://www.youtube.com/watch?v=x", FileName = "Video", Kind = DownloadKind.Video, Video = new VideoOptions { Format = "bv*+ba/b", Container = "mp4" } },
        };
        var parsed = ListTransfer.Parse(ListTransfer.ToJson(items));
        Eq(2, parsed.Count, "json entries");
        Eq("file.zip", parsed[0].FileName, "name");
        Eq(DownloadKind.Video, parsed[1].Kind, "kind");
        Eq("mp4", parsed[1].Video?.Container, "video options");
        Eq("https://a/file.zip\r\nhttps://www.youtube.com/watch?v=x\r\n", ListTransfer.ToText(items), "text export");

        var text = "Notlar:\nhttps://x.com/a.zip, ve (https://x.com/b.pdf).\nftp://x.com/c\njavascript:alert(1)\nhttps://x.com/a.zip\n";
        var links = ListTransfer.Parse(text).Select(e => e.Url).ToList();
        Eq("https://x.com/a.zip|https://x.com/b.pdf", string.Join("|", links), "links from text");

        var bad = ListTransfer.Parse("""[{"url":"file:///C:/x"},{"url":"https://ok/1","fileName":"../../evil.exe","referrer":"javascript:x"}]""");
        Eq(1, bad.Count, "only http(s)");
        Eq("evil.exe", bad[0].FileName, "name sanitized");
        Eq(null, bad[0].Referrer, "bad referrer dropped");
    }

    private static void TestProxyParse()
    {
        Eq("http://127.0.0.1:8080/", NetConfig.ParseProxy("127.0.0.1:8080")?.ToString(), "host:port");
        Eq("http://proxy.local:3128/", NetConfig.ParseProxy(" http://proxy.local:3128 ")?.ToString(), "http url");
        Eq("socks5://127.0.0.1:1080/", NetConfig.ParseProxy("socks5://127.0.0.1:1080")?.ToString().TrimEnd('/') + "/", "socks5");
        foreach (var bad in new[] { "", "proxy.local", "http://proxy.local", "ftp://x:21", "http://u:p@x:8080", "http://x:8080/path", "x:99999", "javascript:alert(1)" })
            Assert(NetConfig.ParseProxy(bad) == null, "refused: " + bad);
        Eq("example.com", NetConfig.NormalizeHost(" https://Example.com/path "), "host from url");
        Eq("example.com", NetConfig.NormalizeHost(".example.com"), "leading dot");
    }

    private static void TestExtractLinks()
    {
        var page = new Uri("https://site.test/dir/index.html");
        var html = """
            <a href="a.zip">1</a><A HREF='/b.pdf'>2</A><a href=c.mp3>3</a><a href="#x">4</a><a href="javascript:void(0)">5</a>
            <a href="get?id=1&amp;n=2">6</a><img src="//cdn.test/i.jpg"><a href="mailto:x@y">7</a><a href="d.zip#frag">8</a>
            """;
        var links = SiteGrabber.ExtractLinks(html, page).Select(u => u.AbsoluteUri).ToList();
        Eq("https://site.test/dir/a.zip|https://site.test/b.pdf|https://site.test/dir/c.mp3|https://site.test/dir/get?id=1&n=2|https://cdn.test/i.jpg|https://site.test/dir/d.zip",
            string.Join("|", links), "links");
        var withBase = SiteGrabber.ExtractLinks("""<base href="https://files.test/x/"><a href="e.zip">e</a>""", page).Select(u => u.AbsoluteUri).ToList();
        Eq("https://files.test/x/e.zip", string.Join("|", withBase), "base href");
    }

    private static void TestConvertPlan()
    {
        const string webm = """
            Input #0, matroska,webm, from 'x.webm':
              Duration: 00:01:02.50, start: 0.000000, bitrate: 900 kb/s
              Stream #0:0(eng): Video: vp9 (Profile 0), yuv420p(tv), 1920x1080, 25 fps
              Stream #0:1(eng): Audio: opus, 48000 Hz, stereo, fltp (default)
            """;
        var info = MediaConverter.ParseInfo(webm);
        Eq("vp9", info.Video, "video codec");
        Eq("opus", info.Audio, "audio codec");
        Eq(TimeSpan.FromSeconds(62.5), info.Duration, "duration");
        var mp4 = string.Join(" ", MediaConverter.CodecArguments(info, ConvertTarget.Mp4)!);
        Assert(mp4.Contains("-c:v libx264") && mp4.Contains("-c:a aac") && mp4.Contains("+faststart"), "vp9/opus → h264/aac: " + mp4);
        var mp3 = string.Join(" ", MediaConverter.CodecArguments(info, ConvertTarget.Mp3)!);
        Assert(mp3.Contains("-vn") && mp3.Contains("libmp3lame"), "→ mp3: " + mp3);

        var h264 = MediaConverter.ParseInfo("  Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p\n  Stream #0:1[0x2](und): Audio: aac (LC) (mp4a / 0x6134706D), 44100 Hz");
        var copy = string.Join(" ", MediaConverter.CodecArguments(h264, ConvertTarget.Mp4)!);
        Assert(copy.Contains("-c:v copy") && copy.Contains("-c:a copy"), "h264/aac copied: " + copy);

        var song = MediaConverter.ParseInfo("  Stream #0:0: Audio: mp3, 44100 Hz, stereo\n  Stream #0:1: Video: mjpeg (Baseline), yuvj420p, 500x500, 90k tbr (attached pic)");
        Eq(null, song.Video, "cover picture is not a video");
        Assert(MediaConverter.CodecArguments(song, ConvertTarget.Mp4) == null, "no mp4 from a song");
        Assert(string.Join(" ", MediaConverter.CodecArguments(song, ConvertTarget.Mp3)!).Contains("-c:a copy"), "mp3 stays mp3 (copied)");
    }

    private static void TestSecurityHelpers()
    {
        foreach (var n in new[] { "a.msc", "b.chm", "c.library-ms", "d.settingcontent-ms", "invoice.pdf.lnk", "x.appinstaller", "y.xll", "z.EXE" })
            Assert(FileCategories.IsExecutable(n), "warned: " + n);
        foreach (var n in new[] { "a.pdf", "b.zip", "c.mp4", "d.txt" })
            Assert(!FileCategories.IsExecutable(n), "not warned: " + n);
        var err = "ERROR: Unable to download https://cdn.example.com/v.mp4?token=SECRET123&sig=abc (caused by https://x.com/a?k=SECRET456)";
        var red = FileNames.RedactUrlsIn(err);
        Assert(!red.Contains("SECRET") && red.Contains("https://cdn.example.com/v.mp4?…") && red.StartsWith("ERROR: Unable", StringComparison.Ordinal), "redacted: " + red);
        if (OperatingSystem.IsWindows())
        {
            var flags = Hardening.ImageLoadFlags();
            Eq(Hardening.NoRemoteImages | Hardening.NoLowMandatoryLabelImages | Hardening.PreferSystem32Images, flags & 7, "image load policy in force");
        }
    }

    private static async Task TestToolJob()
    {
        if (!OperatingSystem.IsWindows()) { throw new SkippedException("job objects: Windows only"); }
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        // cmd waits for input, so the job is in place before it tries to start whoami.exe.
        async Task<int> RunCmd(bool contained)
        {
            var psi = new ProcessStartInfo(cmd, "/q") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            using var job = contained ? ToolJob.Contain(p, singleProcess: true) : null;
            if (contained)
            {
                Assert(job != null, "job created");
                Assert(job!.Holds(p, out var limit), "process is in the job");
                Eq(1u, limit, "one process at most");
            }
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            await p.StandardInput.WriteLineAsync(Path.Combine(Environment.SystemDirectory, "whoami.exe"));
            await p.StandardInput.WriteLineAsync("exit %errorlevel%");
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            return p.ExitCode;
        }
        Eq(0, await RunCmd(contained: false), "without the job whoami runs");
        Assert(await RunCmd(contained: true) != 0, "inside the job no other program can start");

        // Closing the job ends the tool (and anything it started).
        var ping = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "PING.EXE"), "-n 60 127.0.0.1") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
        _ = ping.StandardOutput.ReadToEndAsync();
        var j = ToolJob.Contain(ping, singleProcess: false)!;
        j.Dispose();
        await ping.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert(ping.HasExited, "tool ended with its job");
    }

    private static void TestFindHash()
    {
        var sums = "aaaa  x\n" + new string('b', 64) + "  yt-dlp.exe\n" + new string('c', 64) + " *yt-dlp_x86.exe\n";
        Eq(new string('b', 64), ToolManager.FindHash(sums, "yt-dlp.exe"), "exact name");
        Eq(null, ToolManager.FindHash(sums, "missing.exe"), "missing");
        // Deno's Windows checksum file as published for v2.9.8 (PowerShell Get-FileHash | Format-List, CRLF).
        var deno = "\r\nAlgorithm : SHA256\r\nHash      : CBA84B26E0DF5AE7F74FC66B86A95B866872A86292EBD84FA1E959927EDD93BF\r\n" +
                   "Path      : C:\\a\\deno\\deno\\target\\release\\deno-x86_64-pc-windows-msvc.zip\r\n\r\n";
        Eq("cba84b26e0df5ae7f74fc66b86a95b866872a86292ebd84fa1e959927edd93bf", ToolManager.FindHash(deno, "deno-x86_64-pc-windows-msvc.zip"), "Deno PowerShell format");
        Eq(null, ToolManager.FindHash(deno, "denort-x86_64-pc-windows-msvc.zip"), "hash of another file is refused");
        Eq("2.9.8", ToolManager.ParseDenoVersion("deno 2.9.8 (stable, release, x86_64-pc-windows-msvc)\nv8 14.2\ntypescript 5.9.2\n"), "deno --version");
        Eq(null, ToolManager.ParseDenoVersion("something else"), "not deno");
    }

    private static void TestCookieContainer()
    {
        var c = Cookies.Build([new CookieData { Name = "a", Value = "1", Domain = "example.com", HostOnly = true, Path = "/" },
                               new CookieData { Name = "b", Value = "x,y;z", Domain = ".example.com", Path = "/" }])!;
        var h1 = c.GetCookieHeader(new Uri("https://example.com/f"));
        var h2 = c.GetCookieHeader(new Uri("https://cdn.example.com/f"));
        var h3 = c.GetCookieHeader(new Uri("https://evil.com/f"));
        Assert(h1.Contains("a=1") && h1.Contains("b="), "host gets both: " + h1);
        Assert(!h2.Contains("a=1") && h2.Contains("b="), "subdomain gets only domain cookie: " + h2);
        Eq("", h3, "other site gets nothing");
    }

    // ------------------------------------------------------------------ engine tests

    private static async Task<(DownloadOutcome outcome, string path, HttpDownloader dl)> Download(string url, int connections = 8,
        DownloadJob? job = null, Func<HttpDownloader, Task>? during = null, CancellationToken ct = default, SpeedLimiter? limiter = null, long maxRequestBytes = 0,
        SpeedLimiter? ownLimiter = null)
    {
        var path = Path.Combine(_work, Guid.NewGuid().ToString("N") + ".bin");
        job ??= new DownloadJob { Url = new Uri(url) };
        var dl = new HttpDownloader(job, new EngineOptions { Connections = connections, MaxRetries = 8, RetryDelayScale = 0.05, StallTimeout = TimeSpan.FromSeconds(10), MaxRequestBytes = maxRequestBytes },
            new[] { limiter, ownLimiter }.OfType<SpeedLimiter>().ToArray());
        dl.ResolveTempPath = _ => path;
        var run = dl.RunAsync(ct);
        if (during != null) await during(dl);
        var outcome = await run;
        return (outcome, job.TempPath ?? path, dl);
    }

    private static string Sha(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs)).ToLowerInvariant();
    }

    private static async Task TestParallel()
    {
        // "slow" paces the server (64 KB per 20 ms per connection), so the transfers really overlap even on a
        // very fast machine; unpaced, 8 MB pieces over loopback can finish before the next connection starts.
        const long size = 24L * 1024 * 1024 + 12345;
        var maxWorkers = 0;
        var (o, path, dl) = await Download($"{_srv.Base}/file/{size}?mode=slow", 8, during: async d =>
        {
            for (var i = 0; i < 100 && maxWorkers < 8; i++) { maxWorkers = Math.Max(maxWorkers, d.ActiveWorkers); await Task.Delay(20); }
        });
        Assert(o.Completed, "completed: " + o);
        Eq(size, new FileInfo(path).Length, "size");
        Eq(TestServer.ExpectedSha(size), Sha(path), "sha256");
        // Sampling the client can miss the peak on a fast machine (64 MB over loopback takes ~1 s), so the server's
        // own count of simultaneous transfers counts too.
        Assert(Math.Max(maxWorkers, _srv.MaxConcurrent) >= 4, $"used parallel connections (client max {maxWorkers}, server max {_srv.MaxConcurrent})");
        // On loopback the server can push several MB into socket buffers before a shortened segment's
        // connection is closed; those bytes count as "served" but are discarded. Real links waste far less.
        Assert(_srv.BytesServed < size * 1.15, $"no large re-download ({_srv.BytesServed})");
    }

    private static async Task TestBoundedRequests()
    {
        const long size = 9L * 1024 * 1024 + 777;
        var (o, path, _) = await Download($"{_srv.Base}/file/{size}", 4, maxRequestBytes: 1024 * 1024);
        Assert(o.Completed, "completed: " + o);
        Eq(size, new FileInfo(path).Length, "size");
        Eq(TestServer.ExpectedSha(size), Sha(path), "sha256");
        Assert(_srv.Requests >= 10, $"asked in pieces ({_srv.Requests} requests)");
        Assert(_srv.BytesServed < size * 1.15, $"no large re-download ({_srv.BytesServed})");
    }

    private static async Task TestBoundedWithDrops()
    {
        const long size = 6L * 1024 * 1024;
        var (o, path, _) = await Download($"{_srv.Base}/file/{size}?mode=drop", 4, maxRequestBytes: 512 * 1024);
        Assert(o.Completed, "completed: " + o);
        Eq(TestServer.ExpectedSha(size), Sha(path), "sha256");
    }

    private static async Task TestNoRange()
    {
        const long size = 5_000_000;
        var (o, path, dl) = await Download($"{_srv.Base}/file/{size}?mode=norange", 8);
        Assert(o.Completed, "completed: " + o);
        Assert(!dl.SupportsRanges, "no ranges detected");
        Eq(TestServer.ExpectedSha(size), Sha(path), "sha256");
    }

    private static async Task TestPauseResume()
    {
        const long size = 30L * 1024 * 1024;
        var url = $"{_srv.Base}/file/{size}?mode=slow";
        var job = new DownloadJob { Url = new Uri(url) };
        using var cts = new CancellationTokenSource();
        var trace = new StringBuilder();
        var sw0 = Stopwatch.StartNew();
        var (o1, path, dl1) = await Download(url, 6, job, async d =>
        {
            while (d.Downloaded < size / 3) { trace.Append($"{sw0.ElapsedMilliseconds}:{d.Downloaded / 1024}K/{d.ActiveWorkers} "); await Task.Delay(20); }
            cts.Cancel();
            trace.Append($"cancel@{sw0.ElapsedMilliseconds} ");
        }, cts.Token);
        Assert(o1.Cancelled, "first run cancelled: " + o1 + " trace: " + (trace.Length > 600 ? trace.ToString()[^600..] : trace));
        Assert(dl1.Downloaded < size, "paused before end");
        await Task.Delay(800); // let the server notice the closed sockets before measuring
        var firstBytes = _srv.BytesServed;

        var job2 = new DownloadJob
        {
            Url = new Uri(url), TempPath = path, TotalBytes = job.TotalBytes, SupportsRanges = job.SupportsRanges,
            ETag = job.ETag, LastModified = job.LastModified, FinalUrl = job.FinalUrl, Segments = dl1.SnapshotSegments(),
        };
        var already = job2.Segments.Sum(s => s.Position - s.Start);
        Assert(already > 0, "progress saved");
        var (o2, path2, _) = await Download(url, 6, job2);
        Assert(o2.Completed, "resumed run completed: " + o2);
        Eq(path, path2, "same file");
        Eq(TestServer.ExpectedSha(size), Sha(path), "sha256 after resume");
        var second = _srv.BytesServed - firstBytes;
        Assert(second <= size - already + 2 * 1024 * 1024, $"resume did not re-download finished parts (second run {second}, needed {size - already})");
    }

    private static async Task TestChangedOnResume()
    {
        const long size = 8L * 1024 * 1024;
        // State that claims an older version (different ETag) of the file was half downloaded.
        var path = Path.Combine(_work, "changed.bin");
        File.WriteAllBytes(path, new byte[size]);
        var job = new DownloadJob
        {
            Url = new Uri($"{_srv.Base}/file/{size}?mode=etag2"), TempPath = path, TotalBytes = size, SupportsRanges = true, ETag = "\"v1\"",
            Segments = [new SegmentState { Start = 0, End = size - 1, Position = size / 2 }],
        };
        var (o, p, _) = await Download(job.Url.AbsoluteUri, 4, job);
        Assert(o.Completed, "completed: " + o);
        Eq(TestServer.ExpectedSha(size, 2), Sha(p), "new content, not a mix");
    }

    private static async Task TestEtagGoneOnResume()
    {
        const long size = 8L * 1024 * 1024;
        // Half downloaded while the server said ETag "v1"; now it sends other bytes of the same size and no ETag.
        var path = Path.Combine(_work, "etag-gone.bin");
        File.WriteAllBytes(path, new byte[size]);
        var job = new DownloadJob
        {
            Url = new Uri($"{_srv.Base}/file/{size}?mode=noetag"), TempPath = path, TotalBytes = size, SupportsRanges = true, ETag = "\"v1\"",
            Segments = [new SegmentState { Start = 0, End = size - 1, Position = size / 2 }],
        };
        var (o, p, _) = await Download(job.Url.AbsoluteUri, 4, job);
        Assert(o.Completed, "completed: " + o);
        Eq(TestServer.ExpectedSha(size, 2), Sha(p), "new content, not a mix");
    }

    private static async Task TestDrops()
    {
        const long size = 12L * 1024 * 1024;
        var (o, path, _) = await Download($"{_srv.Base}/file/{size}?mode=drop", 4);
        Assert(o.Completed, "completed: " + o);
        Eq(TestServer.ExpectedSha(size), Sha(path), "sha256");
    }

    private static async Task Test429()
    {
        const long size = 3L * 1024 * 1024;
        var (o, path, _) = await Download($"{_srv.Base}/file/{size}?mode=r429", 4);
        Assert(o.Completed, "completed: " + o);
        Eq(TestServer.ExpectedSha(size), Sha(path), "sha256");
    }

    private static async Task Test404()
    {
        var (o, _, _) = await Download($"{_srv.Base}/missing", 4);
        Assert(!o.Completed && o.Error == DownloadErrorKind.HttpStatus && o.HttpStatus == 404, "404: " + o);
    }

    private static async Task Test403()
    {
        var (o, _, _) = await Download($"{_srv.Base}/file/1000?mode=forbidden", 4);
        Assert(!o.Completed && o.HttpStatus == 403, "403: " + o);
    }

    private static async Task TestChunked()
    {
        const long size = 2_500_000;
        var (o, path, dl) = await Download($"{_srv.Base}/file/{size}?mode=chunked", 8);
        Assert(o.Completed, "completed: " + o);
        Eq(size, dl.TotalBytes, "size learned from EOF");
        Eq(TestServer.ExpectedSha(size), Sha(path), "sha256");
    }

    private static async Task TestRedirect()
    {
        const long size = 4L * 1024 * 1024;
        var target = Uri.EscapeDataString($"{_srv.Base}/file/{size}");
        var job = new DownloadJob { Url = new Uri($"{_srv.Base}/redirect?to={target}") };
        var (o, path, _) = await Download(job.Url.AbsoluteUri, 4, job);
        Assert(o.Completed, "completed: " + o);
        Assert(job.FinalUrl!.AbsolutePath.StartsWith("/file/"), "final url recorded");
        Eq(TestServer.ExpectedSha(size), Sha(path), "sha256");
    }

    private static async Task TestCookiesScoped()
    {
        const long size = 1024 * 1024;
        // Cookie belongs to 127.0.0.1; the file lives on "localhost" after a redirect.
        var cookies = Cookies.Build([new CookieData { Name = "session", Value = "abc", Domain = "127.0.0.1", HostOnly = true, Path = "/" }]);
        var target = Uri.EscapeDataString($"{_srv.BaseLocalhost}/file/{size}");
        var job = new DownloadJob { Url = new Uri($"{_srv.Base}/redirect?to={target}"), Cookies = cookies };
        var (o, _, _) = await Download(job.Url.AbsoluteUri, 2, job);
        Assert(o.Completed, "completed: " + o);
        var log = _srv.CookieLog.ToList();
        Assert(log.Any(l => l.StartsWith("127.0.0.1") && l.Contains("session=abc")), "cookie sent to its host");
        Assert(!log.Any(l => l.StartsWith("localhost") && l.Contains("session")), "cookie NOT sent to other host");

        // And a cookie-protected file works when the cookie is given.
        var job2 = new DownloadJob { Url = new Uri($"{_srv.Base}/file/{size}?mode=cookie"), Cookies = cookies };
        var (o2, p2, _) = await Download(job2.Url.AbsoluteUri, 4, job2);
        Assert(o2.Completed, "cookie file: " + o2);
        Eq(TestServer.ExpectedSha(size), Sha(p2), "sha256");
    }

    private static async Task TestSpeedLimit()
    {
        const long size = 3L * 1024 * 1024;
        var limiter = new SpeedLimiter { BytesPerSecond = 1024 * 1024 };
        var sw = Stopwatch.StartNew();
        var (o, path, _) = await Download($"{_srv.Base}/file/{size}", 4, limiter: limiter);
        sw.Stop();
        Assert(o.Completed, "completed");
        Assert(sw.Elapsed.TotalSeconds >= 2.4, $"took {sw.Elapsed.TotalSeconds:F2}s, expected ≥ 2.4s");
        Assert(sw.Elapsed.TotalSeconds < 6, $"took {sw.Elapsed.TotalSeconds:F2}s, too slow");
        Eq(TestServer.ExpectedSha(size), Sha(path), "sha256");
    }

    private static async Task TestSharedLimitNotWasted()
    {
        // Shared 2 MB/s. Download A also has its own 128 KB/s limit; B has none. A must not use up the shared limit
        // while it waits for its own (before the fix B got almost nothing), so B gets nearly 2 MB/s.
        var shared = new SpeedLimiter { BytesPerSecond = 2 * 1024 * 1024 };
        var own = new SpeedLimiter { BytesPerSecond = 128 * 1024 };
        using var stopA = new CancellationTokenSource();
        var a = Download($"{_srv.Base}/file/{64L * 1024 * 1024}", 4, ct: stopA.Token, limiter: shared, ownLimiter: own);
        await Task.Delay(300);
        const long size = 4L * 1024 * 1024;
        var sw = Stopwatch.StartNew();
        var (o, path, _) = await Download($"{_srv.Base}/file/{size}", 4, limiter: shared);
        sw.Stop();
        stopA.Cancel();
        try { await a; } catch { }
        Assert(o.Completed, "completed");
        Eq(TestServer.ExpectedSha(size), Sha(path), "sha256");
        Assert(sw.Elapsed.TotalSeconds < 3.5, $"B took {sw.Elapsed.TotalSeconds:F2}s for 4 MB under a 2 MB/s shared limit (A wasted the shared limit)");
    }

    private static async Task TestProbe()
    {
        var info = await HttpDownloader.ProbeAsync(new DownloadJob { Url = new Uri($"{_srv.Base}/file/777777?mode=name") }, CancellationToken.None);
        Eq("naïve café üß.bin", info.FileName, "name");
        Eq(777777L, info.TotalBytes, "size");
        Assert(info.SupportsRanges, "ranges");
    }

    /// <summary>One thread running posted work in order, like the app's Dispatcher (thread-pool posts could run out of order).</summary>
    private sealed class TestContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback, object?)> _queue = new();

        public TestContext()
        {
            var thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var (d, state) in _queue.GetConsumingEnumerable())
                {
                    try { d(state); }
                    catch (Exception ex) { Console.Error.WriteLine("Posted work failed: " + ex); }
                }
            }) { IsBackground = true, Name = "TestContext" };
            thread.Start();
        }

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
    }

    private static async Task<(DownloadManager, DownloadItem)> ManagerDownload(string url, string? expectedSha = null, Action<DownloadManager, DownloadItem>? during = null)
    {
        var folder = Path.Combine(_work, "dl-" + Guid.NewGuid().ToString("N")[..6]);
        var settings = new AppSettings { DownloadFolder = folder, ScanWithAttachmentServices = true };
        var mgr = new DownloadManager(settings, new TestContext());
        mgr.EngineOptionsOverride = () => new EngineOptions { Connections = 4, RetryDelayScale = 0.05 };
        DownloadItem? item = null;
        var done = new TaskCompletionSource();
        mgr.Completed += _ => done.TrySetResult();
        mgr.Failed += _ => done.TrySetResult();
        lock (mgr) item = mgr.Add(new AddRequest { Url = url, ExpectedSha256 = expectedSha, Referrer = "https://example.com/page" });
        during?.Invoke(mgr, item);
        await done.Task.WaitAsync(TimeSpan.FromMinutes(1));
        await Task.Delay(300);
        return (mgr, item);
    }

    private static async Task TestManagerEndToEnd()
    {
        const long size = 6L * 1024 * 1024;
        var (mgr, item) = await ManagerDownload($"{_srv.Base}/file/{size}?mode=name");
        Eq(DownloadStatus.Completed, item.Status, "status (error=" + item.Error + ")");
        Eq("naïve café üß.bin", item.FileName, "server name used");
        Eq(Path.Combine(mgr.Settings.DownloadFolder, "Programs"), item.Folder, "IDM-style category folder (.bin → Programs)");
        Assert(File.Exists(item.FullPath), "final file exists");
        Assert(!File.Exists(item.TempPath), "temp file gone");
        Eq(TestServer.ExpectedSha(size), Sha(item.FullPath), "sha256");
        // Attachment Services treats 127.0.0.1 as the local intranet and may not add a mark; write check is done separately.
        var marked = AttachmentScanner.HasZoneIdentifier(item.FullPath);
        var probe = Path.Combine(_work, "motw.txt");
        File.WriteAllText(probe, "x");
        AttachmentScanner.WriteZoneIdentifier(probe, "https://example.com/a.exe", "https://example.com/");
        Assert(AttachmentScanner.HasZoneIdentifier(probe), "Zone.Identifier written");
        mgr.SaveNow();
        var reloaded = Store.LoadDownloads();
        Assert(reloaded.Any(d => d.Id == item.Id && d.Status == DownloadStatus.Completed), "persisted");
        Assert(item.ProtectedCookies == null, "no cookies kept after completion");
        Report.Add($"      (MOTW on 127.0.0.1 download: {(marked ? "yes" : "no — loopback is not an internet zone, expected")})");
        mgr.Dispose();
    }

    private static async Task TestManagerHashMismatch()
    {
        var (mgr, item) = await ManagerDownload($"{_srv.Base}/file/100000", new string('0', 64));
        Eq(DownloadStatus.Error, item.Status, "status");
        Eq("hash", item.Error, "error code");
        mgr.Dispose();
    }

    private static async Task TestManagerRemove()
    {
        var folder = Path.Combine(_work, "rm");
        var mgr = new DownloadManager(new AppSettings { DownloadFolder = folder }, new TestContext());
        DownloadItem item;
        lock (mgr) item = mgr.Add(new AddRequest { Url = $"{_srv.Base}/file/{40L * 1024 * 1024}?mode=slow" });
        for (var i = 0; i < 200 && item.DownloadedBytes < 1024 * 1024; i++) await Task.Delay(25);
        var temp = item.TempPath;
        Assert(File.Exists(temp), "partial file exists while running");
        lock (mgr) mgr.Remove(item, deleteFile: true);
        for (var i = 0; i < 100 && File.Exists(temp); i++) await Task.Delay(50);
        Assert(!File.Exists(temp), "partial file deleted");
        mgr.Dispose();
    }

    private static async Task TestQueueOneAtATime()
    {
        var folder = Path.Combine(_work, "queue");
        var settings = new AppSettings { DownloadFolder = folder, MaxConcurrentDownloads = 3 };
        settings.Normalize();
        settings.Queues.Add(new QueueInfo { Id = "q1", Name = "Night", MaxConcurrent = 1 });
        var mgr = new DownloadManager(settings, new TestContext());
        mgr.EngineOptionsOverride = () => new EngineOptions { Connections = 2, RetryDelayScale = 0.05 };
        var finished = new List<string>();
        mgr.QueueFinished += id => { lock (finished) finished.Add(id); };
        var items = new List<DownloadItem>();
        lock (mgr)
        {
            for (var i = 0; i < 3; i++)
                items.Add(mgr.Add(new AddRequest { Url = $"{_srv.Base}/file/{3L * 1024 * 1024 + i}?mode=slow", StartNow = false, QueueId = "q1" }));
        }
        Assert(items.All(i => i.QueueId == "q1" && i.Status == DownloadStatus.Paused), "added paused to q1");
        lock (mgr) mgr.StartQueue("q1");
        var maxActive = 0;
        for (var t = 0; t < 1200 && !items.All(i => i.Status is DownloadStatus.Completed or DownloadStatus.Error); t++)
        {
            maxActive = Math.Max(maxActive, items.Count(i => i.IsActive));
            await Task.Delay(25);
        }
        Assert(items.All(i => i.Status == DownloadStatus.Completed), "all completed: " + string.Join(",", items.Select(i => i.Status + ":" + i.Error)));
        Eq(1, maxActive, "at most one at a time");
        await Task.Delay(300);
        lock (finished) Eq("q1", string.Join(",", finished), "queue finished reported once");
        Eq(3, mgr.History.Snapshot().Entries.Count(e => e.Url.Contains("mode=slow") && e.Folder.StartsWith(folder)), "recorded in history");
        mgr.Dispose();
    }

    private static async Task TestQueueStopIsNotFinished()
    {
        var settings = new AppSettings { DownloadFolder = Path.Combine(_work, "queue-stop"), MaxConcurrentDownloads = 3, AutoRetry = true };
        settings.Normalize();
        settings.Queues.Add(new QueueInfo { Id = "q2", Name = "Stop", MaxConcurrent = 1 });
        var mgr = new DownloadManager(settings, new TestContext());
        mgr.EngineOptionsOverride = () => new EngineOptions { Connections = 2, RetryDelayScale = 0.05 };
        var finished = new List<string>();
        mgr.QueueFinished += id => { lock (finished) finished.Add(id); };
        DownloadItem item, waiting;
        lock (mgr)
        {
            item = mgr.Add(new AddRequest { Url = $"{_srv.Base}/file/{24L * 1024 * 1024}?mode=slow", StartNow = false, QueueId = "q2" });
            waiting = mgr.Add(new AddRequest { Url = $"{_srv.Base}/file/{1024L * 1024}?mode=slow", StartNow = false, QueueId = "q2" });
            mgr.StartQueue("q2");
        }
        for (var t = 0; t < 400 && item.DownloadedBytes < 256 * 1024; t++) await Task.Delay(25);
        Assert(item.IsActive, "first job running: " + item.Status);
        lock (mgr)
        {
            waiting.Status = DownloadStatus.Error; // as if it had failed with a network error and waits for its retry
            waiting.RetryAt = DateTime.Now.AddSeconds(1);
            mgr.StopQueue("q2");
        }
        for (var t = 0; t < 200 && item.IsActive; t++) await Task.Delay(25);
        Eq(DownloadStatus.Paused, item.Status, "stopped job paused");
        Eq(null, waiting.RetryAt, "retry cancelled by the stop");
        await Task.Delay(1500);
        lock (finished) Eq("", string.Join(",", finished), "no 'queue finished' after a stop");
        Assert(!item.IsActive && !waiting.IsActive, "nothing started again");
        mgr.Dispose();
    }

    private static Task TestPendingHandOver()
    {
        var settings = new AppSettings { DownloadFolder = Path.Combine(_work, "pending") };
        settings.Normalize();
        var mgr = new DownloadManager(settings, new TestContext());
        var url = $"{_srv.Base}/file/{1024 * 1024}?mode=pending";
        DownloadItem pending;
        lock (mgr) pending = mgr.AddPending(new AddRequest { Url = url, Cookies = [] });
        Eq(DownloadStatus.Paused, pending.Status, "waits for the user");
        Eq(null, pending.QueueId, "in no queue (Start queue does not start it)");
        // Already on disk: a new manager (PDM restarted or crashed) still has it.
        var again = new DownloadManager(settings, new TestContext());
        again.Load();
        Assert(again.Items.Any(i => i.Id == pending.Id), "saved before the answer");
        again.Dispose();
        lock (mgr)
        {
            Eq(null, mgr.FindDuplicate(url, [pending]), "the add window does not call it a duplicate of itself");
            Eq(pending, mgr.FindDuplicate(url), "others do see it");
        }
        mgr.Dispose();
        return Task.CompletedTask;
    }

    private static async Task TestHeaderTimeout()
    {
        var old = HttpDownloader.HeaderTimeout;
        HttpDownloader.HeaderTimeout = TimeSpan.FromMilliseconds(700);
        try
        {
            var sw = Stopwatch.StartNew();
            var failure = await Assert2Throws<TimeoutException>(() => HttpDownloader.ProbeAsync(new DownloadJob { Url = new Uri($"{_srv.Base}/file/1024?mode=hang") }, CancellationToken.None));
            sw.Stop();
            Assert(failure, "probe ends with a TimeoutException");
            Assert(sw.Elapsed.TotalSeconds < 5, $"gave up after {sw.Elapsed.TotalSeconds:F1}s");
            // A user cancel is still a cancel, not a timeout.
            using var cts = new CancellationTokenSource(300);
            var cancelled = await Assert2Throws<OperationCanceledException>(() => HttpDownloader.ProbeAsync(new DownloadJob { Url = new Uri($"{_srv.Base}/file/1024?mode=hang") }, cts.Token));
            Assert(cancelled, "user cancel stays a cancel");
        }
        finally { HttpDownloader.HeaderTimeout = old; }
    }

    private static async Task<bool> Assert2Throws<T>(Func<Task> act) where T : Exception
    {
        try { await act(); return false; }
        catch (T) { return true; }
    }

    private static async Task TestGrabberBodyStall()
    {
        var (stall, total) = (SiteGrabber.BodyStall, SiteGrabber.BodyTotal);
        SiteGrabber.BodyStall = TimeSpan.FromMilliseconds(800);
        SiteGrabber.BodyTotal = TimeSpan.FromSeconds(5);
        try
        {
            var sw = Stopwatch.StartNew();
            var found = await new SiteGrabber().RunAsync(new GrabberOptions { StartUrl = _srv.Base + "/stall.html", Depth = 1 }, CancellationToken.None);
            sw.Stop();
            Assert(sw.Elapsed.TotalSeconds < 6, $"gave up after {sw.Elapsed.TotalSeconds:F1}s (limit 0.8 s of silence)");
            Eq(0, found.Count, "nothing found on a page that never arrived");
        }
        finally { (SiteGrabber.BodyStall, SiteGrabber.BodyTotal) = (stall, total); }
    }

    private static void TestReviewSmallFixes()
    {
        // 18: a hostile, absurdly long part number must not throw.
        Assert(!Archives.IsArchive("x.part999999999999999999999.rar"), "huge part number: not volume 1, no exception");
        Assert(Archives.IsArchive("x.part1.rar") && Archives.IsArchive("x.part01.rar"), "volume 1 is opened");
        Assert(!Archives.IsArchive("x.part2.rar"), "volume 2 is not opened on its own");
        // 17: a raw value with commas keeps its characters (inner quotes included).
        var raw = "{\"x\":1,\"y\":2}";
        var jar = Cookies.Build([new CookieData { Name = "session", Value = raw, Domain = "example.org", HostOnly = true }])!;
        var header = jar.GetCookieHeader(new Uri("https://example.org/"));
        Assert(header.Contains(raw, StringComparison.Ordinal), "cookie header keeps the value: " + header);
        Eq("plain", Cookies.EscapeValue("plain"), "plain value untouched");
        Eq("\"a,b\"", Cookies.EscapeValue("a,b"), "value with comma wrapped");
        Eq("\"a,b\"", Cookies.EscapeValue("\"a,b\""), "already wrapped value untouched");
    }

    private static async Task TestProxyRouting()
    {
        var settings = new AppSettings { ProxyMode = ProxyMode.Custom, ProxyAddress = $"127.0.0.1:{_srv.Port}" };
        NetConfig.BypassLocal = false;
        try
        {
            NetConfig.Configure(settings);
            Eq($"http://127.0.0.1:{_srv.Port}", NetConfig.ToolEnvironment()["HTTPS_PROXY"], "yt-dlp proxy environment");
            const long size = 1024 * 1024;
            var (mgr, item) = await ManagerDownload($"{_srv.Base}/file/{size}");
            Eq(DownloadStatus.Completed, item.Status, "status (error=" + item.Error + ")");
            Eq(TestServer.ExpectedSha(size), Sha(item.FullPath), "sha256");
            Assert(_srv.ProxyRequests > 0, "requests went through the proxy");
            mgr.Dispose();

            if (!OperatingSystem.IsWindows()) { throw new SkippedException("proxy login part needs DPAPI: Windows only"); }
            _srv.RequireProxyLogin = true;
            var (m2, failed) = await ManagerDownload($"{_srv.Base}/file/{size}");
            Eq("http:407", failed.Error, "no proxy login → 407");
            m2.Dispose();
            settings.ProxyUser = "proxy";
            settings.ProtectedProxyPassword = Dpapi.Protect("secret");
            NetConfig.Configure(settings);
            Eq($"http://proxy:secret@127.0.0.1:{_srv.Port}", NetConfig.ToolEnvironment()["HTTP_PROXY"], "login in the environment, not the command line");
            var (m3, ok) = await ManagerDownload($"{_srv.Base}/file/{size}");
            Eq(DownloadStatus.Completed, ok.Status, "with proxy login (error=" + ok.Error + ")");
            m3.Dispose();
        }
        finally
        {
            _srv.RequireProxyLogin = false;
            NetConfig.BypassLocal = true;
            NetConfig.Configure(new AppSettings());
        }
    }

    private static async Task TestSiteLogin()
    {
        if (!OperatingSystem.IsWindows()) { throw new SkippedException("site logins need DPAPI: Windows only"); }
        const long size = 300_000;
        try
        {
            var (m1, noLogin) = await ManagerDownload($"{_srv.Base}/auth/{size}");
            Eq("http:401", noLogin.Error, "no login → 401");
            m1.Dispose();

            var settings = new AppSettings();
            settings.Logins.Add(new SiteLogin { Host = "other.test", User = "user", ProtectedPassword = Dpapi.Protect("pass") });
            NetConfig.Configure(settings);
            _srv.AuthLog.Clear();
            var (m2, wrongHost) = await ManagerDownload($"{_srv.Base}/auth/{size}");
            Eq("http:401", wrongHost.Error, "login for another site is not used");
            Assert(_srv.AuthLog.All(a => a.Length == 0), "no password sent to this host");
            m2.Dispose();

            settings.Logins.Add(new SiteLogin { Host = "127.0.0.1", User = "user", ProtectedPassword = Dpapi.Protect("pass") });
            NetConfig.Configure(settings);
            var (m3, ok) = await ManagerDownload($"{_srv.Base}/auth/{size}");
            Eq(DownloadStatus.Completed, ok.Status, "with login (error=" + ok.Error + ")");
            Eq(TestServer.ExpectedSha(size), Sha(ok.FullPath), "sha256");
            m3.Dispose();
        }
        finally { NetConfig.Configure(new AppSettings()); }
    }

    private static async Task TestGrabber()
    {
        async Task<string> Grab(int depth, params string[] types)
        {
            var files = await new SiteGrabber().RunAsync(new GrabberOptions { StartUrl = _srv.Base + "/site/index.html", Depth = depth, Extensions = types.ToList() }, CancellationToken.None);
            return string.Join("|", files.Select(f => f.Url.Replace(_srv.Base, "")).Order(StringComparer.Ordinal));
        }
        Eq("/files/a.zip|/files/pic.jpg", await Grab(0), "depth 0: start page only, css skipped");
        Eq("/dl.php?id=7&x=1|/files/a.zip|/files/b.pdf|/files/pic.jpg", await Grab(1), "depth 1 (+ a file behind dl.php)");
        Eq("/dl.php?id=7&x=1|/files/a.zip", await Grab(1, "zip"), "only zip (dl.php answers d.zip)");
        Eq("/dl.php?id=7&x=1|/files/a.zip|/files/c.zip", await Grab(2, ".zip"), "depth 2 reaches page3 (<base href>)");
        var found = await new SiteGrabber().RunAsync(new GrabberOptions { StartUrl = _srv.Base + "/site/index.html", Depth = 1 }, CancellationToken.None);
        Eq(_srv.Base + "/site/page2.html", found.Single(f => f.Url.EndsWith("b.pdf")).FoundOn, "page a file was found on");
    }

    private static async Task TestConvert()
    {
        var ffmpeg = new[] { "/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg" }.FirstOrDefault(File.Exists);
        if (ffmpeg == null) { throw new SkippedException("no FFmpeg on this machine: conversion end-to-end skipped"); }
        var toolDir = Path.Combine(_work, "tools-conv");
        Directory.CreateDirectory(toolDir);
        File.CreateSymbolicLink(Path.Combine(toolDir, "ffmpeg.exe"), ffmpeg);
        var dir = Path.Combine(_work, "conv");
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "klip.webm");
        var make = Process.Start(new ProcessStartInfo(ffmpeg, ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=duration=3:size=320x240:rate=25",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=3", "-c:v", "libvpx-vp9", "-deadline", "realtime", "-c:a", "libopus", "-shortest", input]) { RedirectStandardError = true })!;
        await make.WaitForExitAsync();
        Assert(File.Exists(input), "test webm made");

        var conv = new MediaConverter(new ToolManager(toolDir));
        var progress = 0.0;
        conv.Progress = p => progress = Math.Max(progress, p);
        var mp4 = await conv.ConvertAsync(input, ConvertTarget.Mp4, CancellationToken.None);
        Eq(Path.Combine(dir, "klip.mp4"), mp4, "mp4 next to the input");
        var info = await conv.ProbeAsync(mp4, CancellationToken.None);
        Eq("h264", info.Video, "mp4 video");
        Eq("aac", info.Audio, "mp4 audio");
        Assert(info.Duration is { TotalSeconds: > 2.5 and < 3.5 }, "duration kept: " + info.Duration);
        Eq(1.0, progress, "progress reached 100 %");
        var mp3 = await conv.ConvertAsync(input, ConvertTarget.Mp3, CancellationToken.None);
        var a = await conv.ProbeAsync(mp3, CancellationToken.None);
        Eq("mp3", a.Audio, "mp3 audio");
        Eq(null, a.Video, "mp3 has no video");
        Assert(!Directory.EnumerateFiles(dir, "*.pdmconv*").Any(), "no temporary files left");
        var again = await conv.ConvertAsync(input, ConvertTarget.Mp4, CancellationToken.None);
        Eq(Path.Combine(dir, "klip (1).mp4"), again, "existing file is not overwritten");
    }

    /// <summary>A stand-in yt-dlp (a script, so not on Windows) that fails like YouTube does when given cookies
    /// (signed-in clients need a JavaScript runtime) and like Instagram does without them.</summary>
    private static async Task TestYtDlpCookieChoice()
    {
        if (OperatingSystem.IsWindows()) { throw new SkippedException("needs a script as yt-dlp: Linux only"); }
        var dir = Path.Combine(_work, "fake-ytdlp");
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, "yt-dlp.exe");
        File.WriteAllText(script, """
            #!/usr/bin/env python3
            import sys, os
            args = sys.argv[1:]
            here = os.path.dirname(os.path.abspath(__file__))
            with open(os.path.join(here, 'calls.log'), 'a') as f:
                f.write(('C ' if '--cookies' in args else 'N ') + ('J' if '-J' in args else 'D') + '\n')
            url = args[-1]
            cookies = '--cookies' in args
            if 'v=age' in url and not cookies:
                sys.stderr.write('ERROR: [youtube] age: Sign in to confirm your age. This video may be inappropriate for some users.\n'); sys.exit(1)
            if 'youtube.com' in url and cookies and 'v=age' not in url:
                sys.stderr.write('ERROR: [youtube] abc: Requested format is not available. Use --list-formats for a list of available formats\n'); sys.exit(1)
            if 'instagram.com' in url and not cookies:
                sys.stderr.write('ERROR: [Instagram] abc: Requested content is not available, rate-limit reached or login required\n'); sys.exit(1)
            if '-J' in args:
                print('{"title":"T","duration":5,"extractor_key":"X","formats":[{"format_id":"1","vcodec":"avc1","acodec":"mp4a","height":720}]}'); sys.exit(0)
            home = [a[5:] for a in args if a.startswith('home:')][0]
            os.makedirs(home, exist_ok=True)  # the real yt-dlp creates its output folder too
            out = os.path.join(home, 'video.mp4')
            open(out, 'wb').write(b'x' * 1000)
            target = args[args.index('--print-to-file') + 2]
            open(target, 'a').write(out + '\n')
            """.Replace("\r", ""));
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var tools = new ToolManager(dir);
        var log = Path.Combine(dir, "calls.log");
        var cookies = new List<CookieData> { new() { Name = "SAPISID", Value = "x", Domain = ".youtube.com", Path = "/" } };

        async Task<(DownloadOutcome, string)> Download(string url)
        {
            File.WriteAllText(log, "");
            var folder = Path.Combine(_work, "fake-out-" + Guid.NewGuid().ToString("N")[..6]);
            var runner = new YtDlpRunner(tools, url, folder, new VideoOptions { Format = YtDlpRunner.FormatFor(720) }, null) { BrowserCookies = cookies };
            var outcome = await runner.RunAsync(CancellationToken.None);
            return (outcome, string.Join(",", File.ReadAllLines(log)));
        }

        var (yt, ytCalls) = await Download("https://www.youtube.com/watch?v=abc");
        Assert(yt.Completed, "YouTube completed: " + yt.Detail);
        Eq("N D", ytCalls, "YouTube: one run, no cookies");

        var (age, ageCalls) = await Download("https://www.youtube.com/watch?v=age");
        Assert(age.Completed, "age-limited completed: " + age.Detail);
        Eq("N D,C D", ageCalls, "YouTube age limit: plain first, then with cookies");

        var (ig, igCalls) = await Download("https://www.instagram.com/p/abc/");
        Assert(ig.Completed, "Instagram completed: " + ig.Detail);
        Eq("C D", igCalls, "login site: cookies first");

        Assert(!YtDlpRunner.CookiesFirst("https://youtu.be/abc") && !YtDlpRunner.CookiesFirst("https://m.youtube.com/watch?v=1") && YtDlpRunner.CookiesFirst("https://x.com/a/status/1"), "CookiesFirst");

        // Review finding 13: two videos that end up with the same file name in one folder. The second job must get its
        // own file ("video (1).mp4") instead of taking the first one's file as its result, and the first stays untouched.
        var sharedFolder = Path.Combine(_work, "fake-same-name");
        Directory.CreateDirectory(sharedFolder);
        File.WriteAllText(Path.Combine(sharedFolder, "video.mp4"), "someone else's file");
        var first = new YtDlpRunner(tools, "https://www.instagram.com/p/one/", sharedFolder, new VideoOptions { Format = YtDlpRunner.FormatFor(720) }, null) { BrowserCookies = cookies, JobId = Guid.NewGuid() };
        var second = new YtDlpRunner(tools, "https://www.instagram.com/p/two/", sharedFolder, new VideoOptions { Format = YtDlpRunner.FormatFor(720) }, null) { BrowserCookies = cookies, JobId = Guid.NewGuid() };
        Assert((await first.RunAsync(CancellationToken.None)).Completed && (await second.RunAsync(CancellationToken.None)).Completed, "both completed");
        Eq("someone else's file", File.ReadAllText(Path.Combine(sharedFolder, "video.mp4")), "an existing file is never replaced");
        Eq(Path.Combine(sharedFolder, "video (1).mp4"), first.OutputPath, "first job: its own file");
        Eq(Path.Combine(sharedFolder, "video (2).mp4"), second.OutputPath, "second job: its own file, not the first one's");
        Eq(1000L, new FileInfo(second.OutputPath!).Length, "second file has the second job's content");
        Assert(!Directory.Exists(YtDlpRunner.JobTempDir("https://www.instagram.com/p/two/", new VideoOptions { Format = YtDlpRunner.FormatFor(720) }, second.JobId)), "job folder cleaned up");

        // Info query ("Get qualities"): the same choice, and the other one when the first fails.
        File.WriteAllText(log, "");
        var info = await YtDlpRunner.GetInfoAsync(tools, "https://www.youtube.com/watch?v=abc", false, CancellationToken.None, cookies);
        Eq("T", info.Title, "youtube info");
        Eq("N J", string.Join(",", File.ReadAllLines(log)), "youtube info without cookies");
        File.WriteAllText(log, "");
        var withLogin = await YtDlpRunner.GetInfoAsync(tools, "https://www.instagram.com/p/abc/", false, CancellationToken.None, cookies);
        Eq("T", withLogin.Title, "instagram info");
        Eq("C J", string.Join(",", File.ReadAllLines(log)), "instagram info with cookies");
    }

    private static async Task TestPipe()
    {
        var name = "PDM.test." + Guid.NewGuid().ToString("N");
        using var server = new PipeServer(name, req => Task.FromResult(Encoding.UTF8.GetBytes("{\"echo\":" + req.Length + "}")));
        server.Start();
        Assert(server.Running, "server running");
        var reply = await PipeClient.SendAsync(name, Encoding.UTF8.GetBytes("{\"type\":\"ping\"}"), TimeSpan.FromSeconds(2));
        Eq("{\"echo\":15}", Encoding.UTF8.GetString(reply!), "reply");
        // A second server with the same name must not be able to take over.
        using var squatter = new PipeServer(name, _ => Task.FromResult(Array.Empty<byte>()));
        squatter.Start();
        Assert(!squatter.Running, "second server with same name refused");
    }

    private static Task<byte[]?> TalkToHost(string hostExe, string origin, byte[] message) =>
        TalkToHost(hostExe, [origin, "--parent-window=0"], message);

    private static async Task<byte[]?> TalkToHost(string hostExe, string[] args, byte[] message)
    {
        var psi = new ProcessStartInfo(hostExe) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdin = p.StandardInput.BaseStream;
        await PipeFraming.WriteAsync(stdin, message, CancellationToken.None);
        var reply = await PipeFraming.ReadAsync(p.StandardOutput.BaseStream, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
        stdin.Close();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        return reply;
    }

    private static async Task TestNativeHost(string hostExe)
    {
        // The host talks to the real per-user pipe name, so run a test server under that name.
        string? seenOrigin = null;
        using var server = new PipeServer(AppPaths.PipeName, req =>
        {
            var msg = BridgeProtocol.Parse(req, Allowed);
            using var doc = JsonDocument.Parse(req);
            seenOrigin = doc.RootElement.GetProperty("origin").GetString();
            return Task.FromResult(msg is DownloadMessage d ? BridgeProtocol.Ok(d.Items.Count) : BridgeProtocol.Ok());
        });
        server.Start();
        if (!server.Running) throw new Exception("PDM app seems to be running (pipe name taken) — close it to run this test");
        var reply = await TalkToHost(hostExe, ExtensionInfo.Origin, Encoding.UTF8.GetBytes("{\"type\":\"download\",\"items\":[{\"url\":\"https://example.com/a.zip\"}]}"));
        using var r = JsonDocument.Parse(reply!);
        Assert(r.RootElement.GetProperty("ok").GetBoolean(), "ok");
        Eq(1, r.RootElement.GetProperty("accepted").GetInt32(), "accepted");
        Eq(ExtensionInfo.Origin, seenOrigin, "origin forwarded by host");
    }

    private static async Task TestNativeHostFirefox(string hostExe)
    {
        string? seenOrigin = null;
        using var server = new PipeServer(AppPaths.PipeName, req =>
        {
            using var doc = JsonDocument.Parse(req);
            seenOrigin = doc.RootElement.GetProperty("origin").GetString();
            return Task.FromResult(BridgeProtocol.Parse(req, [ExtensionInfo.Origin, ExtensionInfo.FirefoxId]) is PingMessage ? BridgeProtocol.Ok() : BridgeProtocol.Error("type"));
        });
        server.Start();
        if (!server.Running) throw new Exception("PDM app seems to be running (pipe name taken) — close it to run this test");
        // Firefox starts the host with the path of its manifest and the calling add-on's ID.
        var reply = await TalkToHost(hostExe, [@"C:\x\com.pdm.host.firefox.json", ExtensionInfo.FirefoxId], Encoding.UTF8.GetBytes("{\"type\":\"ping\"}"));
        using var r = JsonDocument.Parse(reply!);
        Assert(r.RootElement.GetProperty("ok").GetBoolean(), "ok");
        Eq(ExtensionInfo.FirefoxId, seenOrigin, "Firefox add-on ID forwarded as origin");

        var psi = new ProcessStartInfo(hostExe) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true };
        psi.ArgumentList.Add(@"C:\x\com.pdm.host.firefox.json");
        psi.ArgumentList.Add("someone-else@example.com");
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Eq(2, p.ExitCode, "another add-on is refused");
    }

    private static async Task TestNativeHostBadOrigin(string hostExe)
    {
        var psi = new ProcessStartInfo(hostExe) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true };
        psi.ArgumentList.Add("chrome-extension://not-an-id/");
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Eq(2, p.ExitCode, "exit code for bad origin");
    }
}
