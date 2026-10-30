using System.Diagnostics;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Services;

// AI search (ADR-054). A model turns the description into the EXISTING catalogue filters; the normal
// Approved-only catalogue query runs; a model re-ranks at most 10 candidates. The model never picks
// listings, never sees who is asking, and never sees an exact coordinate. Any model failure degrades
// to a plain catalogue search.
//
// LLM calls happen only on page 1. Page 2+ is a plain catalogue continuation (no parse, no rank).
public sealed class AiSearchService : IAiSearchService
{
    public const int MaxDescriptionLength = 500;
    public const int MaxRankCandidates = 10;
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 50;
    private const int MaxExcludeIds = 500;

    private readonly ICurrentUserContext _currentUser;
    private readonly IListingsQueryService _listings;
    private readonly ICategoriesQueryService _categories;
    private readonly IDistrictsQueryService _districts;
    private readonly IHomePointStore _homePoints;
    private readonly IAiSearchModel _model;
    private readonly IAiSearchLogStore _logStore;
    private readonly IAiSearchSettings _settings;
    private readonly TimeProvider _time;

    public AiSearchService(
        ICurrentUserContext currentUser,
        IListingsQueryService listings,
        ICategoriesQueryService categories,
        IDistrictsQueryService districts,
        IHomePointStore homePoints,
        IAiSearchModel model,
        IAiSearchLogStore logStore,
        IAiSearchSettings settings,
        TimeProvider time)
    {
        _currentUser = currentUser;
        _listings = listings;
        _categories = categories;
        _districts = districts;
        _homePoints = homePoints;
        _model = model;
        _logStore = logStore;
        _settings = settings;
        _time = time;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.IsConfigured)
        {
            return false;
        }

        return await GetSpentTodayAsync(cancellationToken) < _settings.DailyBudgetUsd;
    }

    public async Task<ServiceResult<AiSearchResponse>> SearchAsync(
        AiSearchRequest request,
        string language,
        CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.UserId;
        if (userId is null)
        {
            return Fail(AiSearchErrorCodes.Unauthenticated, "Sign in to use AI search.");
        }

        var description = (request.Description ?? string.Empty).Trim();
        if (description.Length == 0 || description.Length > MaxDescriptionLength)
        {
            return Fail(
                AiSearchErrorCodes.InvalidDescription,
                $"The description must be 1 to {MaxDescriptionLength} characters.");
        }

        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize < 1 ? DefaultPageSize : Math.Min(request.PageSize, MaxPageSize);
        var excludeIds = (request.ExcludeIds ?? Array.Empty<Guid>()).Distinct().Take(MaxExcludeIds).ToList();

        if (page >= 2)
        {
            return ServiceResult<AiSearchResponse>.Success(
                await ContinueAsync(userId.Value, request.Filters, page, pageSize, excludeIds, cancellationToken));
        }

        var first = await FirstPageAsync(userId.Value, description, language, request.Filters, pageSize, excludeIds, cancellationToken);
        return first is null
            ? Fail(AiSearchErrorCodes.DailyLimit, "The daily AI search limit is reached. Try again tomorrow.")
            : ServiceResult<AiSearchResponse>.Success(first);
    }

    // ---- Page 2+: no model call of any kind ------------------------------------------------------

    private async Task<AiSearchResponse> ContinueAsync(
        Guid userId,
        AiSearchFilters? filters,
        int page,
        int pageSize,
        IReadOnlyList<Guid> excludeIds,
        CancellationToken cancellationToken)
    {
        var built = await BuildCatalogueFilterAsync(userId, filters ?? new AiSearchFilters(), cancellationToken);
        var result = await QueryAsync(built.Filter, page, pageSize, excludeIds, cancellationToken);

        return new AiSearchResponse
        {
            Items = result.Items,
            Page = page,
            PageSize = result.PageSize,
            TotalCount = result.TotalCount + excludeIds.Count,
            HasMore = result.HasMore,
            AppliedFilters = built.Applied,
            Ranked = false,
            Intent = null,
            LocationNotConsidered = built.LocationNotConsidered
        };
    }

    // ---- Page 1 ----------------------------------------------------------------------------------

    // Returns null when the user's daily AI search limit is reached (no search runs, nothing is logged).
    private async Task<AiSearchResponse?> FirstPageAsync(
        Guid userId,
        string description,
        string language,
        AiSearchFilters? givenFilters,
        int pageSize,
        IReadOnlyList<Guid> excludeIds,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var usage = new Usage();

        // Once a model call has been paid for, a log row is ALWAYS written - even when the request is
        // aborted or a later step throws - so the daily budget sees the spend. (A pre-model refusal has
        // no spend and writes its own row or none.)
        try
        {
            return await FirstPageCoreAsync(
                userId, description, language, givenFilters, pageSize, excludeIds, clock, usage, cancellationToken);
        }
        finally
        {
            if (usage.HasSpend && !usage.Logged)
            {
                await WriteLogAsync(userId, description, AiSearchOutcomes.AiUnavailable, clock, usage, cancellationToken);
            }
        }
    }

    private async Task<AiSearchResponse?> FirstPageCoreAsync(
        Guid userId,
        string description,
        string language,
        AiSearchFilters? givenFilters,
        int pageSize,
        IReadOnlyList<Guid> excludeIds,
        Stopwatch clock,
        Usage usage,
        CancellationToken cancellationToken)
    {
        if (!_settings.IsConfigured)
        {
            return await FinishUnavailableAsync(
                userId, description, pageSize, excludeIds, AiSearchOutcomes.AiUnavailable, clock, usage, cancellationToken);
        }

        if (await GetSpentTodayAsync(cancellationToken) >= _settings.DailyBudgetUsd)
        {
            return await FinishUnavailableAsync(
                userId, description, pageSize, excludeIds, AiSearchOutcomes.BudgetExhausted, clock, usage, cancellationToken);
        }

        // Per-user daily cap (ADR-054): only searches that reached the model count, and only when AI
        // would otherwise run. A refused search is not logged, so it does not count either.
        var usedToday = await _logStore.CountModelSearchesSinceAsync(
            userId, YerevanCalendar.StartOfTodayUtc(_time.GetUtcNow()), cancellationToken);
        if (usedToday >= _settings.PerUserPerDay)
        {
            return null;
        }

        var catalogs = await LoadCatalogsAsync(cancellationToken);

        AiSearchFilters filters;
        AiParsedQuery? parsed = null;
        var widenedDistrictIds = new List<Guid>();
        var purposeWidened = false;
        var datesMentioned = false;
        var locationNotConsidered = false;
        ListingsQueryFilter catalogueFilter;
        AiSearchFilters applied;
        PagedResult<ListingPreviewResponse> result;

        if (givenFilters is null)
        {
            var parseCall = await _model.ParseAsync(description, NormalizeLanguage(language), catalogs, cancellationToken);
            if (parseCall is not null)
            {
                usage.AddParse(parseCall); // billed even when the answer is unusable
            }

            if (parseCall is null || parseCall.IsFailed)
            {
                return await FinishUnavailableAsync(
                    userId, description, pageSize, excludeIds, AiSearchOutcomes.AiUnavailable, clock, usage, cancellationToken);
            }

            parsed = parseCall.Value;
            datesMentioned = parsed.DatesMentioned;
            filters = MapParsedToFilters(parsed, catalogs, _settings.NearMeRadiusKm);

            var built = await BuildCatalogueFilterAsync(userId, filters, cancellationToken);
            locationNotConsidered = built.LocationNotConsidered;
            catalogueFilter = built.Filter;
            applied = built.Applied;
            result = await QueryAsync(catalogueFilter, 1, pageSize, excludeIds, cancellationToken);

            // Auto-widening (ADR-054): only when nothing matched, at most two steps. Age, an explicit
            // price and "only X" districts are never relaxed.
            if (result.TotalCount == 0)
            {
                var widenedFilters = filters;

                // Step 1: add the neighbouring districts of the ones named.
                if (!parsed.DistrictIsHard && filters.DistrictIds is { Count: > 0 })
                {
                    var added = NeighbourDistrictIds(filters.DistrictIds, catalogs);
                    if (added.Count > 0)
                    {
                        widenedDistrictIds.AddRange(added);
                        widenedFilters = With(filters, districtIds: filters.DistrictIds.Concat(added).ToList());
                        built = await BuildCatalogueFilterAsync(userId, widenedFilters, cancellationToken);
                        result = await QueryAsync(built.Filter, 1, pageSize, excludeIds, cancellationToken);
                        catalogueFilter = built.Filter;
                        applied = built.Applied;
                    }
                }

                // Step 2: the purpose is a soft wish. The category was only a best fit for it (a named
                // item type pins the category, so that case is left alone); drop the category and let
                // the ranker weigh the purpose instead.
                if (result.TotalCount == 0
                    && !string.IsNullOrWhiteSpace(parsed.Purpose)
                    && string.IsNullOrWhiteSpace(parsed.ItemType)
                    && widenedFilters.CategoryId is not null)
                {
                    purposeWidened = true;
                    widenedFilters = With(widenedFilters, clearCategory: true);
                    built = await BuildCatalogueFilterAsync(userId, widenedFilters, cancellationToken);
                    result = await QueryAsync(built.Filter, 1, pageSize, excludeIds, cancellationToken);
                    catalogueFilter = built.Filter;
                    applied = built.Applied;
                }
            }
        }
        else
        {
            // Re-rank after the user edited the filters: no parse, those filters stand.
            var built = await BuildCatalogueFilterAsync(userId, givenFilters, cancellationToken);
            locationNotConsidered = built.LocationNotConsidered;
            catalogueFilter = built.Filter;
            applied = built.Applied;
            result = await QueryAsync(catalogueFilter, 1, pageSize, excludeIds, cancellationToken);
        }

        // ---- Rank the first <= 10 ------------------------------------------------------------------
        var items = result.Items.ToList();
        var ranked = false;
        var outcome = AiSearchOutcomes.Ok;
        IReadOnlyCollection<ListingPreviewResponse> ordered = items;

        if (items.Count == 1)
        {
            ranked = true; // nothing to reorder
        }
        else if (items.Count > 1)
        {
            var candidates = (await _listings.GetAiRankCandidatesAsync(
                    catalogueFilter, Math.Min(MaxRankCandidates, items.Count), cancellationToken))
                .Where(candidate => items.Any(item => item.Id == candidate.Id))
                .ToList();

            var rankCall = await _model.RankAsync(description, candidates, cancellationToken);
            if (rankCall is not null)
            {
                usage.AddRank(rankCall);
            }

            if (rankCall is null || rankCall.IsFailed)
            {
                outcome = AiSearchOutcomes.RankingFailed;
            }
            else
            {
                var rankedCandidateIds = AiSearchRankValidator.Merge(
                    candidates.Select(candidate => candidate.Id).ToList(), rankCall.Value);
                var rest = items.Select(item => item.Id).Where(id => !rankedCandidateIds.Contains(id));
                var finalIds = rankedCandidateIds.Concat(rest).ToList();
                var byId = items.ToDictionary(item => item.Id);
                ordered = finalIds.Select(id => byId[id]).ToList();
                ranked = true;
            }
        }

        string? intent = null;
        if (ranked && parsed is not null)
        {
            intent = AiSearchIntentValidator.Validate(parsed.Intent, DistrictNames(catalogs));
        }

        await WriteLogAsync(userId, description, outcome, clock, usage, cancellationToken);

        return new AiSearchResponse
        {
            Items = ordered,
            Page = 1,
            PageSize = result.PageSize,
            TotalCount = result.TotalCount + excludeIds.Count,
            HasMore = result.HasMore,
            AppliedFilters = applied,
            Ranked = ranked,
            Intent = intent,
            WidenedDistrictIds = widenedDistrictIds,
            PurposeWidened = purposeWidened,
            DatesMentioned = datesMentioned,
            LocationNotConsidered = locationNotConsidered,
            AiUnavailable = false
        };
    }

    // The AI could not run: a plain catalogue text search on the description, flagged so the client
    // can say so. Never an error status (ADR-054).
    private async Task<AiSearchResponse> FinishUnavailableAsync(
        Guid userId,
        string description,
        int pageSize,
        IReadOnlyList<Guid> excludeIds,
        string outcome,
        Stopwatch clock,
        Usage usage,
        CancellationToken cancellationToken)
    {
        var filter = new ListingsQueryFilter { Search = description };
        var result = await QueryAsync(filter, 1, pageSize, excludeIds, cancellationToken);
        await WriteLogAsync(userId, description, outcome, clock, usage, cancellationToken);

        return new AiSearchResponse
        {
            Items = result.Items,
            Page = 1,
            PageSize = result.PageSize,
            TotalCount = result.TotalCount + excludeIds.Count,
            HasMore = result.HasMore,
            AppliedFilters = new AiSearchFilters(),
            Ranked = false,
            Intent = null,
            AiUnavailable = true
        };
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    private sealed record BuiltFilter(ListingsQueryFilter Filter, AiSearchFilters Applied, bool LocationNotConsidered);

    // AiSearchFilters -> the catalogue's own filter. "Near me" is resolved here from the signed-in
    // user's home point; those coordinates go to the catalogue query only, never to the model and never
    // back to the client (ADR-008, ADR-053).
    private async Task<BuiltFilter> BuildCatalogueFilterAsync(
        Guid userId,
        AiSearchFilters filters,
        CancellationToken cancellationToken)
    {
        var ageGroup = AiSearchAgeGroups.FromKey(filters.AgeGroup);

        decimal? originLat = null;
        decimal? originLng = null;
        double? radiusKm = null;
        var locationNotConsidered = false;

        if (filters.NearHome)
        {
            var user = await _homePoints.FindUserByIdAsync(userId, cancellationToken);
            if (user?.HomeLatitude is { } lat && user.HomeLongitude is { } lng)
            {
                originLat = lat;
                originLng = lng;
                radiusKm = filters.RadiusKm ?? _settings.NearMeRadiusKm;
            }
            else
            {
                locationNotConsidered = true;
            }
        }

        var districtIds = filters.DistrictIds is { Count: > 0 } ? filters.DistrictIds.Distinct().ToList() : null;

        var catalogueFilter = new ListingsQueryFilter
        {
            CategoryId = filters.CategoryId,
            MaxPrice = filters.MaxPrice,
            AgeFromMonths = ageGroup?.FromMonths,
            AgeToMonths = ageGroup?.ToMonths,
            DistrictIds = districtIds,
            OriginLat = originLat,
            OriginLng = originLng,
            RadiusKm = radiusKm
        };

        var applied = new AiSearchFilters
        {
            CategoryId = filters.CategoryId,
            AgeGroup = ageGroup?.Key,
            MaxPrice = filters.MaxPrice,
            DistrictIds = districtIds,
            NearHome = originLat is not null,
            RadiusKm = radiusKm
        };

        return new BuiltFilter(catalogueFilter, applied, locationNotConsidered);
    }

    private async Task<PagedResult<ListingPreviewResponse>> QueryAsync(
        ListingsQueryFilter filter,
        int page,
        int pageSize,
        IReadOnlyList<Guid> excludeIds,
        CancellationToken cancellationToken)
    {
        // With excludeIds the client sends every id it already shows, so the remainder always starts
        // at its first row: offset paging on top of that would skip results. Without excludeIds the
        // requested page is a plain offset page.
        var effectivePage = excludeIds.Count > 0 ? 1 : page;

        return await _listings.GetApprovedListingsAsync(
            new ListingsQueryFilter
            {
                City = filter.City,
                CategoryId = filter.CategoryId,
                MinPrice = filter.MinPrice,
                MaxPrice = filter.MaxPrice,
                Search = filter.Search,
                AgeFromMonths = filter.AgeFromMonths,
                AgeToMonths = filter.AgeToMonths,
                OriginLat = filter.OriginLat,
                OriginLng = filter.OriginLng,
                RadiusKm = filter.RadiusKm,
                DistrictIds = filter.DistrictIds,
                ExcludeIds = excludeIds.Count > 0 ? excludeIds : null,
                Page = effectivePage,
                PageSize = pageSize
            },
            cancellationToken);
    }

    private static AiSearchFilters MapParsedToFilters(AiParsedQuery parsed, AiSearchCatalogs catalogs, double nearMeRadiusKm)
    {
        // Unknown slugs/codes are dropped: the model may only choose from the lists it was given.
        Guid? categoryId = null;
        if (!string.IsNullOrWhiteSpace(parsed.CategorySlug))
        {
            var match = catalogs.CategoryIdsBySlug.TryGetValue(parsed.CategorySlug, out var id);
            categoryId = match ? id : null;
        }

        var districtIds = parsed.DistrictCodes
            .Select(code => catalogs.DistrictIdsByCode.TryGetValue(code, out var id) ? (Guid?)id : null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        var ageGroup = parsed.AgeMonths is { } months ? AiSearchAgeGroups.FromMonths(months) : null;

        return new AiSearchFilters
        {
            CategoryId = categoryId,
            AgeGroup = ageGroup?.Key,
            MaxPrice = parsed.MaxPricePerDay is > 0 ? parsed.MaxPricePerDay : null,
            DistrictIds = districtIds.Count > 0 ? districtIds : null,
            NearHome = parsed.NearMe,
            RadiusKm = parsed.NearMe ? nearMeRadiusKm : null
        };
    }

    private static List<Guid> NeighbourDistrictIds(IReadOnlyList<Guid> districtIds, AiSearchCatalogs catalogs)
    {
        var selected = new HashSet<Guid>(districtIds);
        var added = new List<Guid>();

        foreach (var districtId in districtIds)
        {
            if (!catalogs.DistrictCodesById.TryGetValue(districtId, out var code))
            {
                continue;
            }

            foreach (var neighbourCode in YerevanDistrictAdjacency.NeighboursOf(code))
            {
                if (catalogs.DistrictIdsByCode.TryGetValue(neighbourCode, out var neighbourId)
                    && selected.Add(neighbourId))
                {
                    added.Add(neighbourId);
                }
            }
        }

        return added;
    }

    private static AiSearchFilters With(AiSearchFilters source, IReadOnlyList<Guid>? districtIds = null, bool clearCategory = false) =>
        new()
        {
            CategoryId = clearCategory ? null : source.CategoryId,
            AgeGroup = source.AgeGroup,
            MaxPrice = source.MaxPrice,
            DistrictIds = districtIds ?? source.DistrictIds,
            NearHome = source.NearHome,
            RadiusKm = source.RadiusKm
        };

    private static IEnumerable<string> DistrictNames(AiSearchCatalogs catalogs) =>
        catalogs.Districts.SelectMany(d => new[] { d.NameEn, d.NameRu, d.NameHy });

    private static string NormalizeLanguage(string? language) =>
        language is "ru" or "hy" ? language : "en";

    private async Task<AiSearchCatalogs> LoadCatalogsAsync(CancellationToken cancellationToken)
    {
        var categories = await _categories.GetAllAsync(cancellationToken);
        var districts = await _districts.GetAllAsync(cancellationToken);

        return new AiSearchCatalogs(
            categories.Select(c => new AiCatalogCategory(c.Slug, c.Name)).ToList(),
            districts.Select(d => new AiCatalogDistrict(d.Code, d.NameEn, d.NameHy, d.NameRu)).ToList())
        {
            CategoryIdsBySlug = categories.ToDictionary(c => c.Slug, c => c.Id),
            DistrictIdsByCode = districts.ToDictionary(d => d.Code, d => d.Id),
            DistrictCodesById = districts.ToDictionary(d => d.Id, d => d.Code)
        };
    }

    private async Task<decimal> GetSpentTodayAsync(CancellationToken cancellationToken) =>
        await _logStore.GetSpentSinceAsync(YerevanCalendar.StartOfTodayUtc(_time.GetUtcNow()), cancellationToken);

    private async Task WriteLogAsync(
        Guid userId,
        string description,
        string outcome,
        Stopwatch clock,
        Usage usage,
        CancellationToken cancellationToken)
    {
        // A paid-for call must reach the log even if the request was aborted meanwhile.
        var token = usage.HasSpend ? CancellationToken.None : cancellationToken;
        usage.Logged = true;

        await _logStore.AddAsync(
            new AiSearchLog
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CreatedAt = _time.GetUtcNow().UtcDateTime,
                Description = description,
                ParseModel = usage.ParseModel,
                RankModel = usage.RankModel,
                InputTokens = usage.InputTokens,
                OutputTokens = usage.OutputTokens,
                CostUsd = usage.CostUsd,
                LatencyMs = (int)clock.ElapsedMilliseconds,
                Outcome = outcome
            },
            token);
    }

    private static ServiceResult<AiSearchResponse> Fail(string code, string message) =>
        ServiceResult<AiSearchResponse>.Failure(new ServiceError { Code = code, Message = message });

    private sealed class Usage
    {
        public string? ParseModel { get; private set; }
        public string? RankModel { get; private set; }
        public int InputTokens { get; private set; }
        public int OutputTokens { get; private set; }
        public decimal CostUsd { get; private set; }

        // A model call was made (billed or estimated), successful or not.
        public bool HasSpend { get; private set; }
        public bool Logged { get; set; }

        public void AddParse<T>(AiModelResult<T> call)
        {
            ParseModel = call.Model;
            Add(call.InputTokens, call.OutputTokens, call.CostUsd);
        }

        public void AddRank<T>(AiModelResult<T> call)
        {
            RankModel = call.Model;
            Add(call.InputTokens, call.OutputTokens, call.CostUsd);
        }

        private void Add(int input, int output, decimal cost)
        {
            HasSpend = true;
            InputTokens += input;
            OutputTokens += output;
            CostUsd += cost;
        }
    }
}
