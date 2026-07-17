using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Domain.Entities;

public sealed class Listing
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid CategoryId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal PricePerDay { get; set; }

    // The rental period the price applies to. Non-nullable; defaults to Daily so existing rows and
    // omitted requests behave exactly as before. Booking-cost math still treats this as per-day.
    public PriceUnit PriceUnit { get; set; } = PriceUnit.Daily;
    public string Currency { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string? AddressLine { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public ListingStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Moderation fields — populated by admin actions (approve / reject).
    // RejectionReason is the composed, human-readable reason (label + optional note) used for
    // email + legacy display; RejectionReasonCode / RejectionNote keep the structured parts so
    // the owner UI can render a localized reason chip and the moderator's note separately.
    public string? RejectionReason { get; set; }
    public string? RejectionReasonCode { get; set; }
    public string? RejectionNote { get; set; }
    public DateTime? ModeratedAt { get; set; }
    public Guid? ModeratedByUserId { get; set; }

    // Toy-rental MVP: optional, additive metadata. All fields are nullable so existing
    // generic-listing rows remain valid and the create-listing contract stays backward compatible.
    public int? AgeFromMonths { get; set; }
    public int? AgeToMonths { get; set; }
    public string? Condition { get; set; }
    public string? HygieneNotes { get; set; }
    public string? SafetyNotes { get; set; }
    public decimal? DepositAmount { get; set; }
    public int? MinRentalDays { get; set; }
    public DeliveryType? DeliveryType { get; set; }

    public User Owner { get; set; } = null!;
    public Category Category { get; set; } = null!;
    public ICollection<ListingImage> Images { get; set; } = new List<ListingImage>();
    public ICollection<Booking> Bookings { get; set; } = new List<Booking>();
    public ICollection<Favorite> Favorites { get; set; } = new List<Favorite>();
}
