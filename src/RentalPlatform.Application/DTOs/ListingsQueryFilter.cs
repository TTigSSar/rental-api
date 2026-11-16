using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace RentalPlatform.Application.DTOs;

public sealed class ListingsQueryFilter : IValidatableObject
{
    public const int MaxCategoryIds = 50;
    public const int MaxAgeRanges = 5;
    public const int MaxConditions = 4;
    private const int MaxAgeMonths = 1200;

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

    // ADR-066: multi-select filters, bound from repeated query keys. User input, so malformed or
    // oversized lists are rejected (400) in Validate rather than silently truncated.
    // CategoryIds is merged with the legacy single CategoryId; a listing matches when its category
    // is in the set and visible.
    public IReadOnlyList<Guid>? CategoryIds { get; init; }

    // "{fromMonths}-{toMonths}"; the upper bound may be empty for open-ended ("72-"). A listing
    // matches when it overlaps ANY range. Combined with the legacy AgeFromMonths/AgeToMonths as AND.
    public IReadOnlyList<string>? AgeRanges { get; init; }

    // New | LikeNew | Good | Fair (ADR-042), case-insensitive. Legacy stored values are normalised
    // in the query (Excellent -> LikeNew, Used -> Fair).
    public IReadOnlyList<string>? Conditions { get; init; }

    // newest (default) | price_asc | price_desc | rating_desc | nearest. nearest needs an origin.
    public string? Sort { get; init; }

    // Maps P2-1: viewport (bounding-box) search. Applied only when all four are present; filters
    // against the public (fuzzed) coordinate pair, never the exact one — see ADR-008.
    public decimal? MinLat { get; init; }
    public decimal? MaxLat { get; init; }
    public decimal? MinLng { get; init; }
    public decimal? MaxLng { get; init; }

    // AI search continuation (ADR-054): ids already shown, left out of the result. Set only by
    // AiSearchService; the API layer refuses to bind it from the public query string.
    public IReadOnlyList<Guid>? ExcludeIds { get; init; }

    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;

    /// <summary>Legacy CategoryId merged with CategoryIds, distinct. Empty when no category filter.</summary>
    public IReadOnlyList<Guid> ResolveCategoryIds()
    {
        var ids = new List<Guid>();
        if (CategoryId.HasValue)
        {
            ids.Add(CategoryId.Value);
        }

        if (CategoryIds is not null)
        {
            ids.AddRange(CategoryIds);
        }

        return ids.Distinct().ToList();
    }

    /// <summary>Parsed age ranges, distinct; null when any entry is malformed.</summary>
    public IReadOnlyList<ListingAgeRange>? ParseAgeRanges()
    {
        var result = new List<ListingAgeRange>();
        foreach (var raw in AgeRanges ?? [])
        {
            var parts = (raw ?? string.Empty).Trim().Split('-');
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var from) ||
                from > MaxAgeMonths)
            {
                return null;
            }

            int? to = null;
            if (parts[1].Length > 0)
            {
                if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedTo) ||
                    parsedTo > MaxAgeMonths || parsedTo < from)
                {
                    return null;
                }

                to = parsedTo;
            }

            var range = new ListingAgeRange(from, to);
            if (!result.Contains(range))
            {
                result.Add(range);
            }
        }

        return result;
    }

    /// <summary>Parsed conditions, distinct; null when any entry is not one of the four values.</summary>
    public IReadOnlyList<ListingConditionKind>? ParseConditions()
    {
        var result = new List<ListingConditionKind>();
        foreach (var raw in Conditions ?? [])
        {
            var text = (raw ?? string.Empty).Trim();
            if (text.Length == 0 || char.IsDigit(text[0]) || text.Contains(',') ||
                !Enum.TryParse<ListingConditionKind>(text, ignoreCase: true, out var kind) ||
                !Enum.IsDefined(kind))
            {
                return null;
            }

            if (!result.Contains(kind))
            {
                result.Add(kind);
            }
        }

        return result;
    }

    /// <summary>Parsed sort; null when the value is unknown. Blank means newest.</summary>
    public ListingSortOrder? ParseSort()
    {
        if (string.IsNullOrWhiteSpace(Sort))
        {
            return ListingSortOrder.Newest;
        }

        return Sort.Trim().ToLowerInvariant() switch
        {
            "newest" => ListingSortOrder.Newest,
            "price_asc" => ListingSortOrder.PriceAsc,
            "price_desc" => ListingSortOrder.PriceDesc,
            "rating_desc" => ListingSortOrder.RatingDesc,
            "nearest" => ListingSortOrder.Nearest,
            _ => null
        };
    }

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

        if (ResolveCategoryIds().Count > MaxCategoryIds)
        {
            yield return new ValidationResult(
                $"At most {MaxCategoryIds} categories may be selected.", new[] { nameof(CategoryIds) });
        }

        var ageRanges = ParseAgeRanges();
        if (ageRanges is null)
        {
            yield return new ValidationResult(
                "ageRanges entries must look like '{fromMonths}-{toMonths}' (toMonths may be empty) with from <= to.",
                new[] { nameof(AgeRanges) });
        }
        else if (ageRanges.Count > MaxAgeRanges)
        {
            yield return new ValidationResult(
                $"At most {MaxAgeRanges} age ranges may be selected.", new[] { nameof(AgeRanges) });
        }

        var conditions = ParseConditions();
        if (conditions is null)
        {
            yield return new ValidationResult(
                "conditions must each be one of New, LikeNew, Good, Fair.", new[] { nameof(Conditions) });
        }
        else if (conditions.Count > MaxConditions)
        {
            yield return new ValidationResult(
                $"At most {MaxConditions} conditions may be selected.", new[] { nameof(Conditions) });
        }

        var sort = ParseSort();
        if (sort is null)
        {
            yield return new ValidationResult(
                "sort must be one of newest, price_asc, price_desc, rating_desc, nearest.", new[] { nameof(Sort) });
        }
        else if (sort == ListingSortOrder.Nearest && !(OriginLat.HasValue && OriginLng.HasValue))
        {
            yield return new ValidationResult(
                "sort=nearest requires originLat and originLng.", new[] { nameof(Sort) });
        }
    }
}

public readonly record struct ListingAgeRange(int FromMonths, int? ToMonths);

public enum ListingConditionKind
{
    New = 1,
    LikeNew = 2,
    Good = 3,
    Fair = 4
}

public enum ListingSortOrder
{
    Newest = 0,
    PriceAsc = 1,
    PriceDesc = 2,
    RatingDesc = 3,
    Nearest = 4
}
