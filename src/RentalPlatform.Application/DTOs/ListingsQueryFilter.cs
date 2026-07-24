using System.ComponentModel.DataAnnotations;

namespace RentalPlatform.Application.DTOs;

public sealed class ListingsQueryFilter : IValidatableObject
{
    public string? City { get; init; }
    public Guid? CategoryId { get; init; }
    public decimal? MinPrice { get; init; }
    public decimal? MaxPrice { get; init; }
    public string? Search { get; init; }
    public int? AgeFromMonths { get; init; }
    public int? AgeToMonths { get; init; }
    public decimal? OriginLat { get; init; }
    public decimal? OriginLng { get; init; }
    public double? RadiusKm { get; init; }

    // Maps P1-7: multi-select district filter. Bound from repeated query keys
    // (?districtIds=<guid>&districtIds=<guid>). Capped to the fixed district catalog size at the
    // query-service level — this is user input, not trusted to be bounded.
    public IReadOnlyList<Guid>? DistrictIds { get; init; }

    // Maps P2-1: viewport (bounding-box) search. Applied only when all four are present; filters
    // against the public (fuzzed) coordinate pair, never the exact one — see ADR-008.
    public decimal? MinLat { get; init; }
    public decimal? MaxLat { get; init; }
    public decimal? MinLng { get; init; }
    public decimal? MaxLng { get; init; }

    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // Antimeridian-crossing viewports (MinLng > MaxLng) are not supported — Yerevan is nowhere
        // near the antimeridian, and silently returning an empty result set for this input would be
        // the worst of both worlds. Reject explicitly instead.
        if (MinLng.HasValue && MaxLng.HasValue && MinLng.Value > MaxLng.Value)
        {
            yield return new ValidationResult(
                "MinLng must be less than or equal to MaxLng; antimeridian-crossing viewports are not supported.",
                new[] { nameof(MinLng), nameof(MaxLng) });
        }
    }
}
