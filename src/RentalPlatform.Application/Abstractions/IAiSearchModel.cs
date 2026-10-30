namespace RentalPlatform.Application.Abstractions;

// The seam to the LLM provider (ADR-054). Implementations never throw: any failure (no key, non-2xx,
// timeout, refusal, unusable JSON) is a null result, or a Failed result when money was spent anyway,
// and the service degrades to a plain search.
// Nothing here carries user identity or coordinates; callers pass only the description, the catalogue
// reference lists and public listing fields.
public interface IAiSearchModel
{
    Task<AiModelResult<AiParsedQuery>?> ParseAsync(
        string description,
        string language,
        AiSearchCatalogs catalogs,
        CancellationToken cancellationToken = default);

    /// <summary>Returns candidate ids, best match first. May contain unknown or missing ids; the caller validates.</summary>
    Task<AiModelResult<IReadOnlyList<Guid>>?> RankAsync(
        string description,
        IReadOnlyList<AiRankCandidate> candidates,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A model call that was (or may have been) billed. <see cref="IsFailed"/> means the provider was paid but the
/// answer is unusable (or the call timed out / was aborted: then the cost is a pessimistic estimate); Value
/// must not be read. A plain null from the seam means nothing was spent.
/// </summary>
public sealed record AiModelResult<T>(T Value, string Model, int InputTokens, int OutputTokens, decimal CostUsd)
{
    public bool IsFailed { get; init; }

    public static AiModelResult<T> Failed(string model, int inputTokens, int outputTokens, decimal costUsd) =>
        new(default!, model, inputTokens, outputTokens, costUsd) { IsFailed = true };
}

public sealed record AiCatalogCategory(string Slug, string Name);

public sealed record AiCatalogDistrict(string Code, string NameEn, string NameHy, string NameRu);

public sealed record AiSearchCatalogs(
    IReadOnlyList<AiCatalogCategory> Categories,
    IReadOnlyList<AiCatalogDistrict> Districts)
{
    // Lookups for the service that maps the model's slugs/codes back to ids. The model itself only
    // ever receives Categories and Districts (names and slugs, no ids).
    public IReadOnlyDictionary<string, Guid> CategoryIdsBySlug { get; init; } = new Dictionary<string, Guid>();
    public IReadOnlyDictionary<string, Guid> DistrictIdsByCode { get; init; } = new Dictionary<string, Guid>();
    public IReadOnlyDictionary<Guid, string> DistrictCodesById { get; init; } = new Dictionary<Guid, string>();
}

public sealed record AiParsedQuery(
    int? AgeMonths,
    string? CategorySlug,
    int? MaxPricePerDay,
    bool PriceIsHard,
    IReadOnlyList<string> DistrictCodes,
    bool DistrictIsHard,
    bool NearMe,
    bool DatesMentioned,
    string? ItemType,
    string? Purpose,
    string? Intent);

/// <summary>Public listing fields only. Description is already trimmed to 200 characters.</summary>
public sealed record AiRankCandidate(
    Guid Id,
    string Title,
    string Category,
    decimal PricePerDay,
    int? AgeFromMonths,
    int? AgeToMonths,
    string Description);
