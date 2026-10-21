using System.Globalization;

namespace DotMarc.Reporting;

/// <summary>How dates and times are shown: UK format, in UK time (GMT or BST). Values are stored in UTC; everything a
/// person reads goes through here, so the server's own culture and time zone never show through.</summary>
public static class UkTime
{
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-GB");

    private static readonly TimeZoneInfo Zone = FindZone();

    public static DateTimeOffset ToUk(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, Zone);

    /// <summary>21/10/2026</summary>
    public static string Date(DateTimeOffset value) => ToUk(value).ToString("dd/MM/yyyy", Culture);

    /// <summary>21/10/2026 14:05</summary>
    public static string DateTime(DateTimeOffset value) => ToUk(value).ToString("dd/MM/yyyy HH:mm", Culture);

    /// <summary>21/10/2026 14:05:09</summary>
    public static string DateTimeSeconds(DateTimeOffset value) => ToUk(value).ToString("dd/MM/yyyy HH:mm:ss", Culture);

    /// <summary>14:05</summary>
    public static string Time(DateTimeOffset value) => ToUk(value).ToString("HH:mm", Culture);

    private static TimeZoneInfo FindZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc; // no time zone data where dotMARC runs
        }
    }
}
