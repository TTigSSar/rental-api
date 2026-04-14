using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Domain.Entities;

public sealed class Booking
{
    public Guid Id { get; set; }
    public Guid ListingId { get; set; }
    public Guid RenterId { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public decimal TotalPrice { get; set; }
    public BookingStatus Status { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Listing Listing { get; set; } = null!;
    public User Renter { get; set; } = null!;
}
