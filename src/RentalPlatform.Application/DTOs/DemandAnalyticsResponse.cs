namespace RentalPlatform.Application.DTOs;

/// <summary>
/// Aggregated demand (P3-3): booking requests per Yerevan day, district and category over an inclusive
/// range. From/To echo the resolved range. Pseudonymous aggregate data, admin only.
/// </summary>
public sealed class DemandAnalyticsResponse
{
    public DateOnly From { get; init; }

    public DateOnly To { get; init; }

    /// <summary>Ordered by Day, DistrictId (null first), CategoryId. Empty when there is no demand.</summary>
    public IReadOnlyList<DemandRowResponse> Rows { get; init; } = Array.Empty<DemandRowResponse>();
}

public sealed class DemandRowResponse
{
    public DateOnly Day { get; init; }

    /// <summary>Null when the listing had no district at request time. Serialized as null, never omitted.</summary>
    public Guid? DistrictId { get; init; }

    /// <summary>May no longer resolve to a category (categories can be deleted; history keeps the id).</summary>
    public Guid CategoryId { get; init; }

    public int BookingRequests { get; init; }
}
