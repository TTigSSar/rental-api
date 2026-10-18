namespace RentalPlatform.Domain;

/// <summary>
/// ADR-043: the single place that counts rental days and decides which nights a booking occupies.
/// The return day is free for handover; a same-day booking counts as one day.
/// </summary>
public static class RentalPeriod
{
    /// <summary>max(1, end - start) in days. Requires end >= start (validated by the caller).</summary>
    public static int Days(DateOnly start, DateOnly end) => Math.Max(1, end.DayNumber - start.DayNumber);

    /// <summary>
    /// Exclusive end of the occupied nights [start, effectiveEnd): the end date, or start + 1 for a
    /// same-day booking.
    /// </summary>
    public static DateOnly EffectiveEnd(DateOnly start, DateOnly end) => end > start ? end : start.AddDays(1);

    /// <summary>Two bookings conflict iff a.start &lt; b.effEnd and b.start &lt; a.effEnd.</summary>
    public static bool Overlaps(DateOnly startA, DateOnly endA, DateOnly startB, DateOnly endB) =>
        startA < EffectiveEnd(startB, endB) && startB < EffectiveEnd(startA, endA);
}
