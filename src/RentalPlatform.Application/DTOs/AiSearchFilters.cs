namespace RentalPlatform.Application.DTOs;

public sealed class AiSearchFilters
{
    public Guid? CategoryId { get; init; }

    /// <summary>An existing catalogue age-group key: 0-12, 12-36, 36-72, 72-120 or 120+.</summary>
    public string? AgeGroup { get; init; }

    public decimal? MaxPrice { get; init; }
    public IReadOnlyList<Guid>? DistrictIds { get; init; }

    /// <summary>
    /// "Near me": the server resolves the signed-in user's home point; the coordinates themselves
    /// are never part of this contract.
    /// </summary>
    public bool NearHome { get; init; }

    public double? RadiusKm { get; init; }
}
