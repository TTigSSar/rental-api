namespace RentalPlatform.Domain;

// Yerevan calendar day boundaries (ADR-048): Yerevan is a fixed UTC+4 with no DST, so "today" in
// Yerevan is derived from an injected TimeProvider instant by plain arithmetic, never from
// TimeZoneInfo or the machine's local zone.
public static class YerevanCalendar
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(4);

    /// <summary>The UTC instant at which the current Yerevan calendar day began.</summary>
    public static DateTime StartOfTodayUtc(DateTimeOffset utcNow)
    {
        var yerevanNow = utcNow.UtcDateTime + Offset;
        return DateTime.SpecifyKind(yerevanNow.Date - Offset, DateTimeKind.Utc);
    }
}
