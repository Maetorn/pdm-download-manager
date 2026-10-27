using System.Runtime.InteropServices;

namespace PDM.Core.Security;

public enum ScanResult
{
    /// <summary>Marked as downloaded from the internet and accepted by the antivirus.</summary>
    Ok,
    /// <summary>Antivirus or policy blocked the file (it may have been deleted or quarantined).</summary>
    Blocked,
    /// <summary>Attachment Services failed; Mark-of-the-Web was written manually instead.</summary>
    MarkedOnly,
}

/// <summary>
/// Hands a finished download to Windows Attachment Services, the same API browsers use. It writes the
/// Mark-of-the-Web (Zone.Identifier) so SmartScreen and Office Protected View apply, and asks the
/// registered antivirus to scan the file.
/// </summary>
public static class AttachmentScanner
{
    private static readonly Guid ClientGuid = new("5b7c3f8e-1d2a-4c6b-9e0f-7a1d3c5e9b21");

    public static ScanResult Scan(string path, string sourceUrl, string? referrer)
    {
        var source = FileNames.WithoutUserInfo(sourceUrl);
        int hr;
        try
        {
            hr = RunOnSta(() =>
            {
                var ae = (IAttachmentExecute)new AttachmentServices();
                try
                {
                    var guid = ClientGuid;
                    ae.SetClientGuid(ref guid);
                    ae.SetClientTitle("PDM");
                    ae.SetLocalPath(path);
                    ae.SetSource(source);
                    if (!string.IsNullOrEmpty(referrer) && FileNames.IsHttpUrl(referrer, out _))
                        ae.SetReferrer(FileNames.WithoutUserInfo(referrer!));
                    return ae.Save();
                }
                finally
                {
                    Marshal.FinalReleaseComObject(ae);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"Attachment Services unavailable: {ex.GetType().Name}");
            WriteZoneIdentifier(path, source, referrer);
            return ScanResult.MarkedOnly;
        }

        if (hr >= 0 && File.Exists(path)) return ScanResult.Ok;
        if (!File.Exists(path)) return ScanResult.Blocked;
        // Save() failed but the file is still there: treat as blocked by policy/AV (e.g. INET_E_SECURITY_PROBLEM).
        Log.Warn($"Attachment Services returned 0x{hr:X8}");
        return ScanResult.Blocked;
    }

    /// <summary>Writes the Zone.Identifier alternate data stream (zone 3 = Internet).</summary>
    public static void WriteZoneIdentifier(string path, string sourceUrl, string? referrer)
    {
        try
        {
            var text = "[ZoneTransfer]\r\nZoneId=3\r\n";
            if (!string.IsNullOrEmpty(referrer) && FileNames.IsHttpUrl(referrer, out _))
                text += "ReferrerUrl=" + FileNames.WithoutUserInfo(referrer!) + "\r\n";
            if (FileNames.IsHttpUrl(sourceUrl, out _))
                text += "HostUrl=" + FileNames.WithoutUserInfo(sourceUrl) + "\r\n";
            File.WriteAllText(path + ":Zone.Identifier", text);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not write Zone.Identifier: {ex.GetType().Name}");
        }
    }

    public static bool HasZoneIdentifier(string path)
    {
        try { return File.Exists(path) && File.ReadAllText(path + ":Zone.Identifier").Contains("ZoneId=3"); }
        catch { return false; }
    }

    private static T RunOnSta<T>(Func<T> func)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { result = func(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        // A scan of a very large archive can take a while.
        if (!t.Join(TimeSpan.FromMinutes(10))) throw new TimeoutException("Attachment scan timed out.");
        if (error != null) throw error;
        return result;
    }

    [ComImport, Guid("4125dd96-e03a-4103-8f70-e0597d803b9c")]
    private class AttachmentServices { }

    [ComImport, Guid("73db1241-1e85-4581-8e4f-a81e1d0f8c57"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAttachmentExecute
    {
        void SetClientTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetClientGuid(ref Guid guid);
        void SetLocalPath([MarshalAs(UnmanagedType.LPWStr)] string pszLocalPath);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void SetSource([MarshalAs(UnmanagedType.LPWStr)] string pszSource);
        void SetReferrer([MarshalAs(UnmanagedType.LPWStr)] string pszReferrer);
        [PreserveSig] int CheckPolicy();
        [PreserveSig] int Prompt(IntPtr hwnd, int prompt, out int paction);
        [PreserveSig] int Save();
        [PreserveSig] int Execute(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string? pszVerb, out IntPtr phProcess);
        [PreserveSig] int SaveWithUI(IntPtr hwnd);
        void ClearClientState();
    }
}
