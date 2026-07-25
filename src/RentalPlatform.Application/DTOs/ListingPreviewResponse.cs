using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.DTOs;

public sealed class ListingPreviewResponse
{
    public Guid Id { get; init; }
    public Guid CategoryId { get; init; }
    public string CategoryName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public decimal PricePerDay { get; init; }
    public PriceUnit PriceUnit { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public string? PrimaryImageUrl { get; init; }
    public int? AgeFromMonths { get; init; }
    public int? AgeToMonths { get; init; }
    public string? Condition { get; init; }
    public DateTime CreatedAt { get; init; }

    /// <summary>Average toy rating, or null when below the aggregate threshold.</summary>
    public double? Rating { get; init; }
    public int ReviewCount { get; init; }

    /// <summary>
    /// Great-circle distance in kilometres from the request's OriginLat/OriginLng to this
    /// listing's PUBLIC (geohash-fuzzed) coordinate — never the exact one, for the same reason
    /// the distance filter itself uses the public pair (see ListingsQueryService, ADR-008).
    /// Null whenever the request did not supply an origin, or the listing has no public
    /// coordinate to measure from.
    /// </summary>
    public double? DistanceKm { get; init; }
}
