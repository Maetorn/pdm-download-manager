using System.Text.Json;
using Microsoft.Win32;

namespace PDM.Core.Ipc;

/// <summary>
/// Registers the native messaging host for the current user only (HKCU, no admin rights).
/// Opera reads the Chrome and Chromium keys (verified in opera_browser.dll); Edge has its own key.
/// Firefox has its own key and manifest format (allowed_extensions with the add-on ID).
/// Each manifest only allows our extension's fixed ID.
/// </summary>
public static class NativeHostRegistration
{
    public static readonly string[] RegistryRoots =
    {
        @"Software\Google\Chrome\NativeMessagingHosts",
        @"Software\Chromium\NativeMessagingHosts",
        @"Software\Microsoft\Edge\NativeMessagingHosts",
    };

    public const string FirefoxRegistryRoot = @"Software\Mozilla\NativeMessagingHosts";

    public static string ManifestPath => Path.Combine(AppPaths.HostDir, AppPaths.NativeHostName + ".json");
    public static string FirefoxManifestPath => Path.Combine(AppPaths.HostDir, AppPaths.NativeHostName + ".firefox.json");
    public static string HostExePath => Path.Combine(AppPaths.HostDir, "PDM.NativeHost.exe");
    /// <summary>Tells the host which PDM.exe to start when the app is not running.</summary>
    public static string AppPathFile => Path.Combine(AppPaths.HostDir, "app-path.txt");

    public static void Register(IEnumerable<string> allowedOrigins, string appExePath)
    {
        Directory.CreateDirectory(AppPaths.HostDir);
        File.WriteAllText(ManifestPath, ChromiumManifest(allowedOrigins, HostExePath));
        File.WriteAllText(FirefoxManifestPath, FirefoxManifest(HostExePath));
        File.WriteAllText(AppPathFile, appExePath);
        foreach (var root in RegistryRoots)
        {
            using var key = Registry.CurrentUser.CreateSubKey(root + "\\" + AppPaths.NativeHostName);
            key.SetValue("", ManifestPath);
        }
        using (var ff = Registry.CurrentUser.CreateSubKey(FirefoxRegistryRoot + "\\" + AppPaths.NativeHostName))
            ff.SetValue("", FirefoxManifestPath);
    }

    /// <summary>Chrome/Edge/Opera manifest: only chrome-extension:// origins (anything else would make it invalid).</summary>
    public static string ChromiumManifest(IEnumerable<string> allowedOrigins, string hostExe) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["name"] = AppPaths.NativeHostName,
            ["description"] = "PDM Download Manager bridge",
            ["path"] = hostExe,
            ["type"] = "stdio",
            ["allowed_origins"] = allowedOrigins.Where(o => o.StartsWith("chrome-extension://", StringComparison.Ordinal)).Distinct().ToArray(),
        }, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>Firefox manifest: the add-on is named by its ID (allowed_extensions).</summary>
    public static string FirefoxManifest(string hostExe) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["name"] = AppPaths.NativeHostName,
            ["description"] = "PDM Download Manager bridge",
            ["path"] = hostExe,
            ["type"] = "stdio",
            ["allowed_extensions"] = new[] { ExtensionInfo.FirefoxId },
        }, new JsonSerializerOptions { WriteIndented = true });

    public static void Unregister()
    {
        foreach (var root in RegistryRoots.Append(FirefoxRegistryRoot))
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(root + "\\" + AppPaths.NativeHostName, throwOnMissingSubKey: false); } catch { }
        }
        foreach (var f in new[] { ManifestPath, FirefoxManifestPath, AppPathFile })
            try { if (File.Exists(f)) File.Delete(f); } catch { }
    }

    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryRoots[0] + "\\" + AppPaths.NativeHostName);
            var path = key?.GetValue("") as string;
            return path != null && File.Exists(path) && File.Exists(HostExePath);
        }
        catch { return false; }
    }

    /// <summary>Writes the host exe from the given stream if it is missing or different.</summary>
    public static void EnsureHostExe(Stream source)
    {
        Directory.CreateDirectory(AppPaths.HostDir);
        using var ms = new MemoryStream();
        source.CopyTo(ms);
        var bytes = ms.ToArray();
        if (File.Exists(HostExePath))
        {
            try
            {
                var existing = File.ReadAllBytes(HostExePath);
                if (existing.AsSpan().SequenceEqual(bytes)) return;
            }
            catch { }
        }
        var tmp = HostExePath + ".new";
        File.WriteAllBytes(tmp, bytes);
        try { File.Move(tmp, HostExePath, overwrite: true); }
        catch (IOException)
        {
            // The browser may be running the old host right now; it will be replaced next time.
            try { File.Delete(tmp); } catch { }
        }
    }
}
