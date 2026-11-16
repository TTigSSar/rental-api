namespace RentalPlatform.Application.DTOs;

// ADR-066: counts for the browse sidebar. Each facet is computed with every other active filter
// applied except its own dimension. Zero-count entries are omitted.
public sealed class ListingFacetsResponse
{
    public IReadOnlyList<CategoryFacetCount> Categories { get; init; } = [];
    public IReadOnlyList<DistrictFacetCount> Districts { get; init; } = [];

    /// <summary>Lowest price per day among the matching listings; null when none match.</summary>
    public decimal? MinPricePerDay { get; init; }

    /// <summary>Highest price per day among the matching listings; null when none match.</summary>
    public decimal? MaxPricePerDay { get; init; }
}

public sealed class CategoryFacetCount
{
    public Guid CategoryId { get; init; }
    public int Count { get; init; }
}

public sealed class DistrictFacetCount
{
    public Guid DistrictId { get; init; }
    public int Count { get; init; }
}
