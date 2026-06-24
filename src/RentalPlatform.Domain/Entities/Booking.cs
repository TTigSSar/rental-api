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

    // Timestamp of the owner's approve decision. Powers the booking timeline. Null until approved.
    public DateTime? ApprovedAt { get; set; }

    // Owner's reason when a request is rejected (known reason code or free text). Null otherwise.
    public string? RejectionReason { get; set; }

    // Set when owner marks the toy as handed over (Approved → Active).
    public DateTime? ActiveAt { get; set; }

    // Set when the booking reaches Completed.
    public DateTime? CompletedAt { get; set; }

    public Listing Listing { get; set; } = null!;
    public User Renter { get; set; } = null!;
}
