using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.DTOs;

public sealed class ListingDetailsResponse
{
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public decimal PricePerDay { get; init; }
    public PriceUnit PriceUnit { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;

    // Pickup street address. Gated on the contact-reveal rule: owner and admin always; a renter
    // whose booking reached at least Approved; null for everyone else (incl. anonymous callers).
    // See ListingsQueryService.GetApprovedListingByIdAsync (ContactRevealed / CanSeeExactCoordinates).
    public string? AddressLine { get; init; }

    // Owner/admin get the exact point; every other caller (including anonymous) gets the
    // privacy-safe geohash-cell-centroid pair instead — see
    // ListingsQueryService.GetApprovedListingByIdAsync (P1-3, the public-coordinate rule).
    public decimal? Latitude { get; init; }
    public decimal? Longitude { get; init; }

    // The district the exact point resolved to (point-in-polygon) or the owner's explicit
    // override (P1-4). Not sensitive — visible to every caller regardless of CanSeeExactCoordinates.
    // Null when the exact point is outside all known districts (or the listing has no coordinates).
    public ListingDistrictResponse? District { get; init; }

    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    public int? AgeFromMonths { get; init; }
    public int? AgeToMonths { get; init; }
    public string? Condition { get; init; }
    public string? HygieneNotes { get; init; }
    public string? SafetyNotes { get; init; }
    public decimal? CompensationAmount { get; init; }
    public int? MinRentalDays { get; init; }
    public DeliveryType? DeliveryType { get; init; }

    // Additive multi-select successor to DeliveryType — expanded from Listing.DeliveryOptions in
    // [Pickup, Courier] order, falling back to a single-element list from the legacy DeliveryType
    // for pre-migration rows, or null when neither is set. See DeliveryOptionsMapper.
    // Plain `set` (not `init` like its siblings) because ListingsQueryService.
    // GetApprovedListingByIdAsync fills it in a second step after the EF projection materializes
    // (the expansion is not SQL-translatable) rather than inside the query's object initializer.
    public IReadOnlyList<DeliveryType>? DeliveryTypes { get; set; }

    /// <summary>Average toy rating, or null when below the aggregate threshold.</summary>
    public double? Rating { get; init; }
    public int ReviewCount { get; init; }

    public ListingCategoryResponse Category { get; init; } = new();
    public ListingOwnerResponse Owner { get; init; } = new();
    public IReadOnlyCollection<ListingImageResponse> Images { get; init; } = Array.Empty<ListingImageResponse>();
    public IReadOnlyCollection<ListingBookedDateRangeResponse> BookedDateRanges { get; init; } = Array.Empty<ListingBookedDateRangeResponse>();
}
