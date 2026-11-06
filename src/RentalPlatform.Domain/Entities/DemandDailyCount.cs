namespace RentalPlatform.Domain.Entities;

/// <summary>
/// Aggregated demand signal (P3-3): the number of booking requests created on one Yerevan calendar day
/// for listings in one district and one category, snapshotted at request time. Carries no user, booking
/// or listing id. Deliberately has no foreign keys: history must survive a category delete or a listing
/// move. Written only by booking creation (and the dev seed), inside the same transaction as the booking.
/// </summary>
public sealed class DemandDailyCount
{
    public Guid Id { get; set; }

    /// <summary>The Yerevan calendar day (fixed UTC+4, ADR-048) of the booking's CreatedAt.</summary>
    public DateOnly Day { get; set; }

    /// <summary>Null when the listing had no district at request time. NULLs are equal in the unique key.</summary>
    public Guid? DistrictId { get; set; }

    /// <summary>May no longer resolve to a Category once that category was deleted.</summary>
    public Guid CategoryId { get; set; }

    public int BookingRequests { get; set; }
}
