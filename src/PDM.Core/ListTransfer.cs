using System.Text.Json;
using System.Text.RegularExpressions;
using PDM.Core.Models;
using PDM.Core.Security;

namespace PDM.Core;

/// <summary>One download in an exported list.</summary>
public sealed class ExportEntry
{
    public string Url { get; set; } = "";
    public string? FileName { get; set; }
    public string? Referrer { get; set; }
    public string? PageUrl { get; set; }
    public DownloadKind Kind { get; set; }
    public VideoOptions? Video { get; set; }
}

/// <summary>Export the list as plain addresses (.txt) or with names and video choices (.json); import either.</summary>
public static partial class ListTransfer
{
    public const int MaxImport = 10_000;
    public const long MaxFileBytes = 20L * 1024 * 1024;

    [GeneratedRegex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();

    public static string ToText(IEnumerable<DownloadItem> items) =>
        string.Concat(items.Select(i => i.Url + "\r\n"));

    public static string ToJson(IEnumerable<DownloadItem> items) =>
        JsonSerializer.Serialize(items.Select(i => new ExportEntry
        {
            Url = i.Url,
            FileName = i.FileName,
            Referrer = i.Referrer,
            PageUrl = i.PageUrl,
            Kind = i.Kind,
            Video = i.Video,
        }).ToList(), PdmJson.Default.ListExportEntry);

    /// <summary>Reads a .json export or any text with links in it. Only http/https addresses are kept.</summary>
    public static List<ExportEntry> Parse(string content)
    {
        var text = content.TrimStart('﻿', ' ', '\t', '\r', '\n');
        List<ExportEntry>? list = null;
        if (text.StartsWith('['))
        {
            try { list = JsonSerializer.Deserialize(text, PdmJson.Default.ListExportEntry); }
            catch (JsonException) { }
        }
        list ??= LinkPattern().Matches(text).Select(m => new ExportEntry { Url = m.Value.TrimEnd('.', ',', ')', ';') }).ToList();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ExportEntry>();
        foreach (var e in list)
        {
            if (e == null || !FileNames.IsHttpUrl(e.Url, out _) || !seen.Add(e.Url)) continue;
            if (e.Kind == DownloadKind.Http) e.Video = null;
            e.FileName = string.IsNullOrWhiteSpace(e.FileName) ? null : FileNames.Sanitize(e.FileName);
            if (e.Referrer != null && !FileNames.IsHttpUrl(e.Referrer, out _)) e.Referrer = null;
            if (e.PageUrl != null && !FileNames.IsHttpUrl(e.PageUrl, out _)) e.PageUrl = null;
            result.Add(e);
            if (result.Count >= MaxImport) break;
        }
        return result;
    }
}
