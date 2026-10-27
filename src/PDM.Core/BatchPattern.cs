using System.Globalization;
using System.Text.RegularExpressions;

namespace PDM.Core;

/// <summary>
/// IDM-style batch links: "https://site/img[001-120].jpg" or "[a-z]" ranges, several ranges allowed
/// (every combination). Numbers keep their leading zeros ([01-10] → 01, 02, … 10).
/// </summary>
public static partial class BatchPattern
{
    [GeneratedRegex(@"\[(?:(\d{1,9})-(\d{1,9})|([a-zA-Z])-([a-zA-Z]))\]")]
    private static partial Regex RangePattern();

    public static bool HasPattern(string? text) => !string.IsNullOrEmpty(text) && RangePattern().IsMatch(text);

    /// <summary>All addresses the pattern stands for, at most <paramref name="max"/>.</summary>
    public static List<string> Expand(string pattern, int max = 10_000)
    {
        var parts = new List<object>(); // string literal or List<string> choices
        var last = 0;
        foreach (Match m in RangePattern().Matches(pattern))
        {
            parts.Add(pattern[last..m.Index]);
            parts.Add(Choices(m));
            last = m.Index + m.Length;
        }
        parts.Add(pattern[last..]);

        var results = new List<string> { "" };
        foreach (var part in parts)
        {
            if (part is string literal)
            {
                for (var i = 0; i < results.Count; i++) results[i] += literal;
                continue;
            }
            var choices = (List<string>)part;
            var next = new List<string>(Math.Min(max, results.Count * choices.Count));
            foreach (var r in results)
            {
                foreach (var c in choices)
                {
                    if (next.Count >= max) break;
                    next.Add(r + c);
                }
                if (next.Count >= max) break;
            }
            results = next;
        }
        return results;
    }

    private static List<string> Choices(Match m)
    {
        var list = new List<string>();
        if (m.Groups[1].Success)
        {
            var a = m.Groups[1].Value;
            var b = m.Groups[2].Value;
            var from = long.Parse(a, CultureInfo.InvariantCulture);
            var to = long.Parse(b, CultureInfo.InvariantCulture);
            var width = a.Length > 1 && a[0] == '0' || b.Length > 1 && b[0] == '0' ? Math.Max(a.Length, b.Length) : 0;
            var step = from <= to ? 1 : -1;
            for (var n = from; list.Count < 100_000; n += step)
            {
                list.Add(n.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0'));
                if (n == to) break;
            }
        }
        else
        {
            var from = m.Groups[3].Value[0];
            var to = m.Groups[4].Value[0];
            var step = from <= to ? 1 : -1;
            for (var c = from; ; c = (char)(c + step))
            {
                list.Add(c.ToString());
                if (c == to) break;
            }
        }
        return list;
    }
}
