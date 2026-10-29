namespace RentalPlatform.Application.DTOs;

// Validated in AiSearchService (not with attributes) so a bad description answers with the
// ai_search.invalid_description errorCode (ADR-045) rather than a ValidationProblemDetails.
public sealed class AiSearchRequest
{
    /// <summary>1-500 characters of free text, any of hy/ru/en/translit.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Null on the first search (the description is parsed into filters). Non-null on a re-rank after
    /// a user filter edit, or on page 2+: those filters are used as given and never re-parsed.
    /// </summary>
    public AiSearchFilters? Filters { get; init; }

    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;

    /// <summary>Page 2+: the ids already shown, so the continuation never repeats a card.</summary>
    public IReadOnlyList<Guid>? ExcludeIds { get; init; }
}
