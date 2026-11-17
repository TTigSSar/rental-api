namespace RentalPlatform.Application.DTOs;

public sealed class AiSearchFilters
{
    public Guid? CategoryId { get; init; }

    /// <summary>An existing catalogue age-group key: 0-12, 12-36, 36-72, 72-120 or 120+.</summary>
    public string? AgeGroup { get; init; }

    /// <summary>
    /// ADR-066 lists (ADR-054 amendment 2026-11-17), same caps and validation as the catalogue.
    /// CategoryIds is OR-ed and merged with the legacy CategoryId; AgeRanges ("fromMonths-toMonths",
    /// upper bound may be empty) is OR-ed and merged with the legacy AgeGroup; Conditions is
    /// New | LikeNew | Good | Fair. In the applied filters the merged result is returned, and the
    /// legacy CategoryId / AgeGroup are still filled when they are a single value.
    /// </summary>
    public IReadOnlyList<Guid>? CategoryIds { get; init; }

    public IReadOnlyList<string>? AgeRanges { get; init; }
    public IReadOnlyList<string>? Conditions { get; init; }

    public decimal? MaxPrice { get; init; }
    public IReadOnlyList<Guid>? DistrictIds { get; init; }

    /// <summary>
    /// "Near me": the server resolves the signed-in user's home point; the coordinates themselves
    /// are never part of this contract.
    /// </summary>
    public bool NearHome { get; init; }

    public double? RadiusKm { get; init; }
}
