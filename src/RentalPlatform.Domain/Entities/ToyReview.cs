namespace RentalPlatform.Domain.Entities;

/// <summary>
/// A renter's review of the toy/listing they rented. Overall rating plus five
/// trust subscores. One per booking. Scores are private (aggregated only);
/// the comment is public.
/// </summary>
public sealed class ToyReview
{
    public Guid Id { get; set; }
    public Guid BookingId { get; set; }
    public Guid ListingId { get; set; }
    public Guid ReviewerId { get; set; }

    public int OverallRating { get; set; }
    public int ConditionRating { get; set; }
    public int CleanlinessRating { get; set; }
    public int ValueForMoneyRating { get; set; }
    public int FunPlayValueRating { get; set; }
    public int DescriptionAccuracyRating { get; set; }

    public string? Comment { get; set; }
    public DateTime CreatedAt { get; set; }

    public Booking Booking { get; set; } = null!;
    public Listing Listing { get; set; } = null!;
    public User Reviewer { get; set; } = null!;
}
