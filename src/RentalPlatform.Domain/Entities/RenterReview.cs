namespace RentalPlatform.Domain.Entities;

/// <summary>
/// An owner's review of the renter. One per booking. Scores are private
/// (aggregated only); the comment is public.
/// </summary>
public sealed class RenterReview
{
    public Guid Id { get; set; }
    public Guid BookingId { get; set; }
    public Guid RenterId { get; set; }
    public Guid ReviewerId { get; set; }

    public int CommunicationRating { get; set; }
    public int ReturnedOnTimeRating { get; set; }
    public int CareOfToyRating { get; set; }
    public int WouldRentAgainRating { get; set; }

    public string? Comment { get; set; }
    public DateTime CreatedAt { get; set; }

    public Booking Booking { get; set; } = null!;
    public User Renter { get; set; } = null!;
    public User Reviewer { get; set; } = null!;
}
