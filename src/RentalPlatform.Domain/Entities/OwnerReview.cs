namespace RentalPlatform.Domain.Entities;

/// <summary>
/// A renter's review of the toy's owner. One per booking. Scores are private
/// (aggregated only); the comment is public. Rated separately from the toy.
/// </summary>
public sealed class OwnerReview
{
    public Guid Id { get; set; }
    public Guid BookingId { get; set; }
    public Guid OwnerId { get; set; }
    public Guid ReviewerId { get; set; }

    public int CommunicationRating { get; set; }
    public int PickupHandoverRating { get; set; }
    public int FriendlinessRating { get; set; }

    public string? Comment { get; set; }
    public DateTime CreatedAt { get; set; }

    public Booking Booking { get; set; } = null!;
    public User Owner { get; set; } = null!;
    public User Reviewer { get; set; } = null!;
}
