namespace DotMarc.Data;

/// <summary>Case-insensitive "contains" searches with ILIKE, with LIKE's own wildcards escaped so "100%" means the text
/// "100%", not "100 then anything".</summary>
public static class LikePattern
{
    public const string Escape = "\\";

    public static string? Contains(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? null
            : "%" + text.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
}
