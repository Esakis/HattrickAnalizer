using System.Globalization;
using System.Text.RegularExpressions;

namespace HattrickAnalizer.Services;

/// <summary>CHPP timestamps without an offset are Swedish wall-clock time (HTT), never server-local time.</summary>
public static class ChppTime
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    public static DateTime? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        // Retain explicit offsets in fixtures and feeds that supply ISO timestamps.
        if (Regex.IsMatch(value, @"(?:Z|[+-]\d{2}:?\d{2})$", RegexOptions.IgnoreCase))
            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var offset)
                ? offset.UtcDateTime : null;
        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return null;
        var local = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        // Do not silently invent an instant during the DST gap or repeated hour.
        if (Zone.IsInvalidTime(local) || Zone.IsAmbiguousTime(local)) return null;
        return TimeZoneInfo.ConvertTimeToUtc(local, Zone);
    }

    public static DateTime LocalDate(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone).Date;
}
