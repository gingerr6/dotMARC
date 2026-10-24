namespace DotMarc.Lookalikes;

/// <summary>A typo or look-alike variant of a domain, and how it differs.</summary>
public sealed record LookalikeCandidate(string Name, string Kind);

/// <summary>The names someone would register to pass as a domain: a letter missing, doubled, swapped or next to it on
/// the keyboard, characters that look alike (rn for m, 1 for l), a hyphen, a trailing s, or the same name under another
/// ending. Only for a registered domain (contoso.com, contoso.co.uk), not a subdomain.</summary>
public static class LookalikeGenerator
{
    public const int MaxCandidates = 400;

    /// <summary>Two-part endings, so contoso.co.uk varies "contoso" and keeps "co.uk".</summary>
    private static readonly string[] TwoPartSuffixes =
        ["co.uk", "org.uk", "me.uk", "ltd.uk", "plc.uk", "net.uk", "ac.uk", "gov.uk", "com.au", "net.au", "org.au", "co.nz", "co.za", "com.br", "co.jp", "com.sg"];

    private static readonly string[] OtherEndings = ["com", "co.uk", "uk", "net", "org", "org.uk", "co", "io", "info", "biz", "eu", "online"];

    private static readonly (string From, string To)[] LookAlikes =
        [("m", "rn"), ("rn", "m"), ("w", "vv"), ("vv", "w"), ("l", "1"), ("l", "i"), ("i", "l"), ("i", "1"), ("o", "0"), ("0", "o"), ("d", "cl"), ("cl", "d"), ("e", "3"), ("s", "5"), ("g", "q")];

    private static readonly string[] KeyboardRows = ["qwertyuiop", "asdfghjkl", "zxcvbnm"];

    /// <summary>The variants of the domain, without the domain itself, or none for a subdomain.</summary>
    public static IReadOnlyList<LookalikeCandidate> For(string domainName)
    {
        var name = domainName.Trim().TrimEnd('.').ToLowerInvariant();
        var suffix = TwoPartSuffixes.FirstOrDefault(two => name.EndsWith("." + two, StringComparison.Ordinal)) ?? name[(name.LastIndexOf('.') + 1)..];
        var label = name.Length > suffix.Length + 1 ? name[..^(suffix.Length + 1)] : "";
        if (label.Length < 3 || label.Contains('.'))
        {
            return [];
        }

        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string variant, string kind, string ending)
        {
            var candidate = $"{variant}.{ending}";
            if (candidate != name && IsValidLabel(variant) && found.Count < MaxCandidates)
            {
                found.TryAdd(candidate, kind);
            }
        }

        foreach (var ending in OtherEndings.Where(ending => ending != suffix))
        {
            Add(label, "Other ending", ending);
        }

        for (var i = 0; i < label.Length; i++)
        {
            Add(label.Remove(i, 1), "Letter missing", suffix);
            Add(label.Insert(i, label[i].ToString()), "Letter doubled", suffix);
            if (i < label.Length - 1)
            {
                Add(Swap(label, i), "Letters swapped", suffix);
            }
            if (i > 0)
            {
                Add(label.Insert(i, "-"), "Hyphen added", suffix);
            }
            foreach (var neighbour in Neighbours(label[i]))
            {
                Add(label.Remove(i, 1).Insert(i, neighbour.ToString()), "Next key along", suffix);
            }
        }

        foreach (var (from, to) in LookAlikes)
        {
            for (var at = label.IndexOf(from, StringComparison.Ordinal); at >= 0; at = label.IndexOf(from, at + 1, StringComparison.Ordinal))
            {
                Add(label.Remove(at, from.Length).Insert(at, to), "Look-alike characters", suffix);
            }
        }

        Add(label + "s", "Letter added", suffix);
        return found.Select(pair => new LookalikeCandidate(pair.Key, pair.Value)).ToList();
    }

    private static string Swap(string label, int i) => string.Concat(label.AsSpan(0, i), label[i + 1].ToString(), label[i].ToString(), label.AsSpan(i + 2));

    private static IEnumerable<char> Neighbours(char key)
    {
        foreach (var row in KeyboardRows)
        {
            var at = row.IndexOf(key);
            if (at < 0)
            {
                continue;
            }
            if (at > 0)
            {
                yield return row[at - 1];
            }
            if (at < row.Length - 1)
            {
                yield return row[at + 1];
            }
        }
    }

    private static bool IsValidLabel(string label) =>
        label.Length is > 0 and <= 63 && label[0] != '-' && label[^1] != '-' && label.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');
}
