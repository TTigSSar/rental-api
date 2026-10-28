namespace RentalPlatform.Application.DTOs;

public sealed class AiSearchResponse
{
    public IReadOnlyCollection<ListingPreviewResponse> Items { get; init; } = Array.Empty<ListingPreviewResponse>();
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public bool HasMore { get; init; }

    public AiSearchFilters AppliedFilters { get; init; } = new();

    /// <summary>True only for page 1 when ranking succeeded.</summary>
    public bool Ranked { get; init; }

    /// <summary>One line in the UI language; null when AI is unavailable or the order is not ranked.</summary>
    public string? Intent { get; init; }

    /// <summary>Neighbouring districts added by auto-widening.</summary>
    public IReadOnlyList<Guid> WidenedDistrictIds { get; init; } = Array.Empty<Guid>();

    public bool PurposeWidened { get; init; }
    public bool DatesMentioned { get; init; }
    public bool LocationNotConsidered { get; init; }

    /// <summary>True: the result is a plain text search on the description.</summary>
    public bool AiUnavailable { get; init; }
}
