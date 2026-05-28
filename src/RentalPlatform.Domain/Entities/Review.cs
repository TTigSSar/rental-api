using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Domain.Entities;

public sealed class Review
{
    public Guid Id { get; set; }
    public Guid BookingId { get; set; }
    public Guid ListingId { get; set; }
    public Guid ReviewerId { get; set; }
    public Guid RevieweeId { get; set; }
    public ReviewerRole ReviewerRole { get; set; }
    public int Rating { get; set; }
    public string? Comment { get; set; }
    public DateTime CreatedAt { get; set; }

    public Booking Booking { get; set; } = null!;
    public Listing Listing { get; set; } = null!;
    public User Reviewer { get; set; } = null!;
    public User Reviewee { get; set; } = null!;
}
