using System.Text.RegularExpressions;

namespace BackloggdLogger.Core;

public static partial class GameNames
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]")]
    private static partial Regex Groups();

    [GeneratedRegex(@"(?i)\s*[\(\[]\s*(?:directx\s*(?:\d+(?:\.\d+)*)?|dx\s*\d+(?:\.\d+)*|d3d\s*(?:\d+(?:\.\d+)*)?|vulkan|opengl|metal|x64|x86|win64|win32|\d+\s*-?\s*bit|beta|alpha|demo|early access|[^\)\]]*\b(?:build|version)\b[^\)\]]*)\s*[\)\]]\s*$")]
    private static partial Regex TrailingTag();

    [GeneratedRegex(@"(?i)(?:\s*[\(\[]\s*(?:disc|disk|cd)\s*\d+(?:\s*(?:of|/)\s*\d+)?\s*[\)\]]|\s+(?:disc|disk|cd)\s*\d+(?:\s*(?:of|/)\s*\d+)?)\s*$")]
    private static partial Regex DiscTag();

    [GeneratedRegex(@"(?i)(?:\s+-\s+|\s+)v\d+(?:\.\d+)*\s*$")]
    private static partial Regex TrailingVersion();

    public static string SearchQuery(string name) =>
        Whitespace().Replace(name.Replace("™", "").Replace("®", "").Replace("©", ""), " ").Trim();

    public static string CleanTitle(string title)
    {
        var s = SearchQuery(title);
        string prev;
        do
        {
            prev = s;
            s = TrailingTag().Replace(s, "").Trim();
            s = DiscTag().Replace(s, "").Trim();
            s = TrailingVersion().Replace(s, "").Trim();
        } while (s != prev);
        return s;
    }

    public static IReadOnlyList<string> SearchCandidates(string name) =>
        SearchPlan(name).Select(c => c.Query).ToList();

    public static IReadOnlyList<SearchCandidate> SearchPlan(string name)
    {
        var list = new List<SearchCandidate>();
        void Add(string c, bool exact)
        {
            c = SearchQuery(c);
            if (c.Length > 0 && !list.Any(x => x.Query == c)) list.Add(new SearchCandidate(c, exact));
        }
        var q = SearchQuery(name);
        Add(q, false);
        var noGroups = SearchQuery(Groups().Replace(q, ""));
        Add(noGroups, false);
        var cut = noGroups.Length;
        foreach (var sep in new[] { " - ", " | ", " : ", ":" })
        {
            var i = noGroups.IndexOf(sep, StringComparison.Ordinal);
            if (i >= 0 && i < cut) cut = i;
        }
        if (cut < noGroups.Length)
        {
            var head = noGroups[..cut].Trim();
            if (head.Length >= 3) Add(head, true);
        }
        var words = noGroups.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 3) Add(string.Join(' ', words.Take(2)).TrimEnd(':', '-', '|', ','), true);
        return list;
    }

    public static string PickQuery(string name)
    {
        var q = SearchQuery(name);
        var clean = CleanTitle(name);
        if (clean == q) return q;
        return SearchPlan(name).Select(c => c.Query).FirstOrDefault(c => c != q) ?? clean;
    }

    static readonly string[] GenericProducts =
    {
        "Unity", "UnityPlayer", "Unreal Engine", "Microsoft Windows Operating System", "Electron", "Godot Engine",
    };

    public static string PickName(string windowTitle, string? productName, string exePath)
    {
        var clean = CleanTitle(windowTitle);
        var product = SearchQuery(productName ?? "");
        if (product.Length > 0
            && !GenericProducts.Contains(product, StringComparer.OrdinalIgnoreCase)
            && !product.Equals(Path.GetFileName(exePath), StringComparison.OrdinalIgnoreCase)
            && !product.Equals(Path.GetFileNameWithoutExtension(exePath), StringComparison.OrdinalIgnoreCase)
            && clean.Contains(product, StringComparison.OrdinalIgnoreCase))
            return product;
        return clean.Length > 0 ? clean : windowTitle;
    }
}

public sealed record SearchCandidate(string Query, bool Exact);
