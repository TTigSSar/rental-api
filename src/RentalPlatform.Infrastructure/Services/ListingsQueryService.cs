using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;

namespace RentalPlatform.Infrastructure.Services;

public sealed class ListingsQueryService : IListingsQueryService
{
    private const int DefaultPage = 1;
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    // Districts are a fixed reference-data catalog (see knowledge/decisions.md) — exactly 12 rows.
    // A caller cannot legitimately need more distinct district ids than that; anything past it is
    // dropped rather than passed through to an unbounded SQL IN (...).
    private const int MaxDistrictIds = 12;

    // AI search continuation (ADR-054): ids already shown, left out of the next page.
    private const int MaxExcludeIds = 500;

    // Maps P2-1: cap on map-pin results. IsTruncated is derived by requesting one extra row and
    // checking whether it came back, so truncation is exact without a second COUNT query.
    private const int MaxMapPins = 500;

    // Radius filter bounds (kilometres). Floor of 0.2km sits just above the geohash-7 public
    // coordinate's own uncertainty (~117-153m cells at Yerevan's latitude, see GeohashSnapper) —
    // tighter than that can't be honestly resolved by data this fuzzed. Ceiling of 20km is a
    // sanity bound for a single-city marketplace. Out-of-range input is clamped, never rejected
    // with a 500 or silently ignored.
    private const double MinRadiusKm = 0.2;
    private const double MaxRadiusKm = 20.0;

    // Mean Earth radius (km) — same constant GeohashSnapperTests uses for cell-size measurement,
    // kept identical so "kilometres" means the same thing across the fuzzing and the filtering.
    private const double EarthRadiusKm = 6371.0088;

    private readonly AppDbContext _dbContext;

    public ListingsQueryService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // Shared predicate chain for both the paged public search and the map-pins endpoint — they
    // must never diverge on what "an approved, publicly-visible listing matching this filter"
    // means; only the projection/shape after this differs.
    // ADR-066 facets: a facet is counted with every filter except its own dimension, so the builder
    // can leave one dimension out (and the viewport box, which facets never take).
    private enum SkippedDimension { None, Category, District, Price }

    private IQueryable<Domain.Entities.Listing> BuildApprovedListingsQuery(
        ListingsQueryFilter filter,
        SkippedDimension skip = SkippedDimension.None,
        bool includeViewport = true)
    {
        // Admin console Phase 2 (narrowed by human review — hiding a category retires the LABEL,
        // not the inventory): a hidden category (Category.IsVisible = false) is excluded from
        // GET /api/categories only. Its listings stay in general browse/search (no category
        // filter), the map-pins endpoint, and the home page — see HomeSectionsQueryService.Approved.
        // The only place a hidden category's listings are excluded is when a caller explicitly
        // filters BY that category id below: the category isn't offered as a choice, so filtering
        // by its id is not a supported path and returns no results. Direct-link access
        // (GetApprovedListingByIdAsync) and the owner's my-listings (ListingsOwnerStore) are
        // unaffected either way.
        var query = _dbContext.Listings
            .AsNoTracking()
            // ADR-056: a deleted owner's listings are archived by the deletion; this keeps them out of the
            // catalogue even if one is ever approved or restored afterwards.
            .Where(listing => listing.Status == ListingStatus.Approved && listing.Owner.DeletedAt == null);

        if (!string.IsNullOrWhiteSpace(filter.City))
        {
            var city = filter.City.Trim();
            query = query.Where(listing => listing.City == city);
        }

        var categoryIds = filter.ResolveCategoryIds();
        if (skip != SkippedDimension.Category && categoryIds.Count > 0)
        {
            // Explicit category-id filter (legacy CategoryId merged with CategoryIds, ADR-066): also
            // require the category itself to be visible (see the comment above
            // BuildApprovedListingsQuery). Shared with the map-pins endpoint, which gets the
            // identical narrowing when it passes a category filter.
            var ids = categoryIds.Take(ListingsQueryFilter.MaxCategoryIds).ToList();
            query = query.Where(listing => ids.Contains(listing.CategoryId) && listing.Category.IsVisible);
        }

        if (skip != SkippedDimension.District && filter.DistrictIds is { Count: > 0 })
        {
            var districtIds = filter.DistrictIds.Distinct().Take(MaxDistrictIds).ToList();
            query = query.Where(listing => listing.DistrictId != null && districtIds.Contains(listing.DistrictId.Value));
        }

        if (filter.ExcludeIds is { Count: > 0 })
        {
            // AI search continuation: bounded like DistrictIds — the list is caller-influenced input.
            var excludeIds = filter.ExcludeIds.Distinct().Take(MaxExcludeIds).ToList();
            query = query.Where(listing => !excludeIds.Contains(listing.Id));
        }

        if (skip != SkippedDimension.Price && filter.MinPrice.HasValue)
        {
            query = query.Where(listing => listing.PricePerDay >= filter.MinPrice.Value);
        }

        if (skip != SkippedDimension.Price && filter.MaxPrice.HasValue)
        {
            query = query.Where(listing => listing.PricePerDay <= filter.MaxPrice.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            query = query.Where(listing =>
                listing.Title.Contains(search) || listing.Description.Contains(search));
        }

        if (filter.AgeFromMonths.HasValue || filter.AgeToMonths.HasValue)
        {
            var reqFrom = filter.AgeFromMonths ?? 0;
            var reqTo = filter.AgeToMonths;
            query = query.Where(listing =>
                (listing.AgeToMonths == null || listing.AgeToMonths >= reqFrom) &&
                (listing.AgeFromMonths == null || reqTo == null || listing.AgeFromMonths <= reqTo));
        }

        // ADR-066: several age ranges, OR-ed, each with the same overlap rule as the legacy pair.
        // Combined with the legacy filter above as AND.
        var ageRanges = filter.ParseAgeRanges();
        if (ageRanges is { Count: > 0 })
        {
            query = query.Where(BuildAgeRangesPredicate(ageRanges.Take(ListingsQueryFilter.MaxAgeRanges).ToList()));
        }

        // ADR-066 / ADR-042: Listing.Condition is a free string, so the stored value is normalised in
        // SQL the way the UI does (trim, lowercase, strip space/_/-), and legacy aliases are accepted.
        var conditions = filter.ParseConditions();
        if (conditions is { Count: > 0 })
        {
            var accepted = conditions
                .Take(ListingsQueryFilter.MaxConditions)
                .SelectMany(ConditionAliases)
                .ToList();
            query = query.Where(listing =>
                listing.Condition != null &&
                accepted.Contains(listing.Condition.Trim().ToLower().Replace(" ", "").Replace("_", "").Replace("-", "")));
        }

        if (filter.OriginLat.HasValue && filter.OriginLng.HasValue && filter.RadiusKm.HasValue)
        {
            // Radius filter over the PUBLIC coordinates only — never the exact Latitude/Longitude
            // (see the public-coordinate rule at P1-3/GetApprovedListingByIdAsync above, and
            // ADR-008). Two independent reasons this must stay true, not just "the fuzzing is
            // there anyway":
            //   1. Filtering on the exact pair turns the endpoint into a boolean in/out-of-radius
            //      oracle for an arbitrary circle the caller chooses. Repeat the query with
            //      different centers/radii and you trilaterate the exact point — precisely the
            //      attack the write-time geohash snap exists to prevent. The oracle only becomes
            //      harmless once it is evaluated against a point that is ALREADY the fuzzed one.
            //   2. The listing-detail map draws its uncertainty circle around PublicLatitude/
            //      PublicLongitude. Filtering by the exact pair would silently disagree with the
            //      circle the renter sees drawn on the map (a listing could appear "in range" on
            //      the map but be excluded from search, or vice versa).
            //
            // Two-stage: a bounding-box pre-filter (translates to a sargable range predicate the
            // composite index on (PublicLatitude, PublicLongitude) can use) narrows the candidate
            // set, then a Haversine refinement turns the square box into the actual circle. Both
            // stages run in SQL — see ListingsQueryServiceFilterTests for the bbox-corner-but-
            // outside-circle regression that would silently reappear if either stage fell back to
            // client evaluation.
            var radiusKm = Math.Clamp(filter.RadiusKm.Value, MinRadiusKm, MaxRadiusKm);
            var originLat = filter.OriginLat.Value;
            var originLng = filter.OriginLng.Value;
            var originLatD = (double)originLat;
            var originLngD = (double)originLng;

            var dLat = radiusKm / 111.0;
            var dLng = radiusKm / (111.0 * Math.Cos(originLatD * Math.PI / 180.0));

            var minLat = originLat - (decimal)dLat;
            var maxLat = originLat + (decimal)dLat;
            var minLng = originLng - (decimal)dLng;
            var maxLng = originLng + (decimal)dLng;

            query = ApplyBoundingBox(query, minLat, maxLat, minLng, maxLng);

            // Haversine circle refinement. Written as one inline expression (rather than a call to
            // a shared helper method) because EF Core can only translate the well-known BCL
            // Math.* calls it recognises inline in the LINQ expression tree — a call to a
            // user-defined method here would either fail to translate or silently fall back to
            // client evaluation. The identical formula is duplicated in the DistanceKm projection
            // below (GetApprovedListingsAsync) for the same reason; keep both in sync if this
            // changes.
            query = query.Where(listing =>
                listing.PublicLatitude != null && listing.PublicLongitude != null &&
                2 * EarthRadiusKm * Math.Asin(Math.Sqrt(
                    Math.Pow(Math.Sin((((double)listing.PublicLatitude!.Value - originLatD) * Math.PI / 180.0) / 2.0), 2.0) +
                    Math.Cos(originLatD * Math.PI / 180.0) * Math.Cos((double)listing.PublicLatitude!.Value * Math.PI / 180.0) *
                    Math.Pow(Math.Sin((((double)listing.PublicLongitude!.Value - originLngD) * Math.PI / 180.0) / 2.0), 2.0)
                )) <= radiusKm);
        }

        if (includeViewport && filter.MinLat.HasValue && filter.MaxLat.HasValue && filter.MinLng.HasValue && filter.MaxLng.HasValue)
        {
            // Maps P2-1 viewport search: same public-coordinate box predicate as the radius filter
            // above, just with caller-supplied bounds instead of ones derived from origin+radius.
            query = ApplyBoundingBox(query, filter.MinLat.Value, filter.MaxLat.Value, filter.MinLng.Value, filter.MaxLng.Value);
        }

        return query;
    }

    private static IEnumerable<string> ConditionAliases(ListingConditionKind kind) => kind switch
    {
        ListingConditionKind.New => ["new"],
        ListingConditionKind.LikeNew => ["likenew", "excellent"],
        ListingConditionKind.Good => ["good"],
        _ => ["fair", "used"]
    };

    // OR of the per-range overlap predicate. Each range becomes its own lambda; the bodies are
    // re-pointed at one shared parameter and OR-ed, which EF translates (Expression.Invoke would not).
    private static Expression<Func<Domain.Entities.Listing, bool>> BuildAgeRangesPredicate(
        IReadOnlyList<ListingAgeRange> ranges)
    {
        var parameter = Expression.Parameter(typeof(Domain.Entities.Listing), "listing");
        Expression? body = null;
        foreach (var range in ranges)
        {
            var reqFrom = range.FromMonths;
            var reqTo = range.ToMonths;
            Expression<Func<Domain.Entities.Listing, bool>> single = listing =>
                (listing.AgeToMonths == null || listing.AgeToMonths >= reqFrom) &&
                (listing.AgeFromMonths == null || reqTo == null || listing.AgeFromMonths <= reqTo);
            var rebound = new ParameterReplacer(single.Parameters[0], parameter).Visit(single.Body);
            body = body is null ? rebound : Expression.OrElse(body, rebound);
        }

        return Expression.Lambda<Func<Domain.Entities.Listing, bool>>(body!, parameter);
    }

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }

    private static IQueryable<Domain.Entities.Listing> ApplyBoundingBox(
        IQueryable<Domain.Entities.Listing> query,
        decimal minLat,
        decimal maxLat,
        decimal minLng,
        decimal maxLng)
    {
        return query.Where(listing =>
            listing.PublicLatitude != null && listing.PublicLongitude != null &&
            listing.PublicLatitude >= minLat && listing.PublicLatitude <= maxLat &&
            listing.PublicLongitude >= minLng && listing.PublicLongitude <= maxLng);
    }

    // ADR-066 server-side sort. Every order ends in CreatedAt desc then Id, so pages never overlap or
    // skip rows. Price is ordered as double: SQLite (tests) refuses decimal in ORDER BY, and on SQL
    // Server the cast is order-preserving for money values.
    private IOrderedQueryable<Domain.Entities.Listing> ApplySort(
        IQueryable<Domain.Entities.Listing> query,
        ListingsQueryFilter filter)
    {
        IOrderedQueryable<Domain.Entities.Listing> ordered;
        switch (filter.ParseSort() ?? ListingSortOrder.Newest)
        {
            case ListingSortOrder.PriceAsc:
                ordered = query.OrderBy(listing => (double)listing.PricePerDay);
                break;
            case ListingSortOrder.PriceDesc:
                ordered = query.OrderByDescending(listing => (double)listing.PricePerDay);
                break;
            case ListingSortOrder.RatingDesc:
                // Same exposure rule as the response Rating: only at >= 2 reviews (ADR-064 §2);
                // listings without one go last. Ordered by the raw average, which never contradicts
                // the rounded value shown on the card.
                ordered = query
                    .OrderBy(listing => _dbContext.ToyReviews.Count(tr => tr.ListingId == listing.Id) >= 2 ? 0 : 1)
                    .ThenByDescending(listing =>
                        _dbContext.ToyReviews.Count(tr => tr.ListingId == listing.Id) >= 2
                            ? (double?)_dbContext.ToyReviews
                                .Where(tr => tr.ListingId == listing.Id)
                                .Average(tr => (double)tr.OverallRating)
                            : null);
                break;
            case ListingSortOrder.Nearest when filter.OriginLat.HasValue && filter.OriginLng.HasValue:
                // Haversine on the PUBLIC pair only (ADR-008); same formula as the radius filter.
                var originLatD = (double)filter.OriginLat.Value;
                var originLngD = (double)filter.OriginLng.Value;
                ordered = query
                    .OrderBy(listing => listing.PublicLatitude == null || listing.PublicLongitude == null ? 1 : 0)
                    .ThenBy(listing => 2 * EarthRadiusKm * Math.Asin(Math.Sqrt(
                        Math.Pow(Math.Sin((((double)listing.PublicLatitude!.Value - originLatD) * Math.PI / 180.0) / 2.0), 2.0) +
                        Math.Cos(originLatD * Math.PI / 180.0) * Math.Cos((double)listing.PublicLatitude!.Value * Math.PI / 180.0) *
                        Math.Pow(Math.Sin((((double)listing.PublicLongitude!.Value - originLngD) * Math.PI / 180.0) / 2.0), 2.0)
                    )));
                break;
            default:
                return query.OrderByDescending(listing => listing.CreatedAt).ThenBy(listing => listing.Id);
        }

        return ordered.ThenByDescending(listing => listing.CreatedAt).ThenBy(listing => listing.Id);
    }

    // ADR-066: sidebar facets. Same predicate chain as the catalogue; each facet leaves out only
    // its own dimension. Category counts additionally count visible categories only (a hidden
    // category is not offered as a choice); district and price follow the catalogue exactly so the
    // numbers add up to what browsing returns.
    public async Task<ListingFacetsResponse> GetFacetsAsync(
        ListingsQueryFilter filter,
        CancellationToken cancellationToken = default)
    {
        var categoryRows = await BuildApprovedListingsQuery(filter, SkippedDimension.Category, includeViewport: false)
            .Where(listing => listing.Category.IsVisible)
            .GroupBy(listing => listing.CategoryId)
            .Select(group => new { CategoryId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        var districtRows = await BuildApprovedListingsQuery(filter, SkippedDimension.District, includeViewport: false)
            .Where(listing => listing.DistrictId != null)
            .GroupBy(listing => listing.DistrictId!.Value)
            .Select(group => new { DistrictId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        // Min/Max over decimal is not supported by SQLite either, so take the first row of an
        // ordered query instead of an aggregate.
        var priceQuery = BuildApprovedListingsQuery(filter, SkippedDimension.Price, includeViewport: false);
        var minPrice = await priceQuery
            .OrderBy(listing => (double)listing.PricePerDay)
            .Select(listing => (decimal?)listing.PricePerDay)
            .FirstOrDefaultAsync(cancellationToken);
        var maxPrice = await priceQuery
            .OrderByDescending(listing => (double)listing.PricePerDay)
            .Select(listing => (decimal?)listing.PricePerDay)
            .FirstOrDefaultAsync(cancellationToken);

        return new ListingFacetsResponse
        {
            Categories = categoryRows
                .Where(row => row.Count > 0)
                .OrderByDescending(row => row.Count).ThenBy(row => row.CategoryId)
                .Select(row => new CategoryFacetCount { CategoryId = row.CategoryId, Count = row.Count })
                .ToList(),
            Districts = districtRows
                .Where(row => row.Count > 0)
                .OrderByDescending(row => row.Count).ThenBy(row => row.DistrictId)
                .Select(row => new DistrictFacetCount { DistrictId = row.DistrictId, Count = row.Count })
                .ToList(),
            MinPricePerDay = minPrice,
            MaxPricePerDay = maxPrice
        };
    }

    public async Task<PagedResult<ListingPreviewResponse>> GetApprovedListingsAsync(
        ListingsQueryFilter filter,
        CancellationToken cancellationToken = default)
    {
        var page = filter.Page < 1 ? DefaultPage : filter.Page;
        var pageSize = filter.PageSize < 1 ? DefaultPageSize : Math.Min(filter.PageSize, MaxPageSize);

        var query = BuildApprovedListingsQuery(filter);

        var totalCount = await query.CountAsync(cancellationToken);

        // Catalogue distance badge (design: "0.4 km" on the listing card). Populated only when the
        // request supplied an origin — independent of whether a RadiusKm filter is also active —
        // and always measured from the PUBLIC coordinate, the same value the radius filter itself
        // uses, so the badge and the filter can never disagree about "how far this listing is".
        // hasOrigin is a query-constant (not row-dependent), so EF folds it into the CASE WHEN
        // guard below rather than re-evaluating it per row.
        var hasOrigin = filter.OriginLat.HasValue && filter.OriginLng.HasValue;
        var originLatD = hasOrigin ? (double)filter.OriginLat!.Value : 0.0;
        var originLngD = hasOrigin ? (double)filter.OriginLng!.Value : 0.0;

        var rows = await ApplySort(query, filter)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(listing => new
            {
                listing.Id,
                listing.CategoryId,
                CategoryName = listing.Category.Name,
                listing.Title,
                listing.PricePerDay,
                listing.PriceUnit,
                listing.Currency,
                listing.Country,
                listing.City,
                PrimaryImageUrl = listing.Images
                    .OrderByDescending(image => image.IsPrimary)
                    .ThenBy(image => image.SortOrder)
                    .Select(image => image.Url)
                    .FirstOrDefault(),
                listing.AgeFromMonths,
                listing.AgeToMonths,
                listing.Condition,
                listing.CreatedAt,
                ReviewCount = _dbContext.ToyReviews.Count(tr => tr.ListingId == listing.Id),
                RatingSum = _dbContext.ToyReviews
                    .Where(tr => tr.ListingId == listing.Id)
                    .Sum(tr => tr.OverallRating),
                // Districts are public and coarse (ADR-008/024); same projection as the home sections.
                District = listing.District == null ? null : new ListingDistrictResponse
                {
                    Id = listing.District.Id,
                    Code = listing.District.Code,
                    NameEn = listing.District.NameEn,
                    NameHy = listing.District.NameHy,
                    NameRu = listing.District.NameRu
                },
                // Same Haversine formula as the radius filter's refinement in
                // BuildApprovedListingsQuery — duplicated for the same EF-translation reason
                // documented there (a shared helper method call would not translate). Keep both in
                // sync if the formula changes.
                DistanceKm = (hasOrigin && listing.PublicLatitude != null && listing.PublicLongitude != null)
                    ? (double?)(2 * EarthRadiusKm * Math.Asin(Math.Sqrt(
                        Math.Pow(Math.Sin((((double)listing.PublicLatitude!.Value - originLatD) * Math.PI / 180.0) / 2.0), 2.0) +
                        Math.Cos(originLatD * Math.PI / 180.0) * Math.Cos((double)listing.PublicLatitude!.Value * Math.PI / 180.0) *
                        Math.Pow(Math.Sin((((double)listing.PublicLongitude!.Value - originLngD) * Math.PI / 180.0) / 2.0), 2.0)
                    )))
                    : null
            })
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(r => new ListingPreviewResponse
            {
                Id = r.Id,
                CategoryId = r.CategoryId,
                CategoryName = r.CategoryName,
                Title = r.Title,
                PricePerDay = r.PricePerDay,
                PriceUnit = r.PriceUnit,
                Currency = r.Currency,
                Country = r.Country,
                City = r.City,
                PrimaryImageUrl = r.PrimaryImageUrl,
                AgeFromMonths = r.AgeFromMonths,
                AgeToMonths = r.AgeToMonths,
                Condition = r.Condition,
                CreatedAt = r.CreatedAt,
                ReviewCount = r.ReviewCount,
                // Aggregate hidden until the minimum number of reviews (2).
                Rating = r.ReviewCount >= 2 ? Math.Round((double)r.RatingSum / r.ReviewCount, 1) : null,
                DistanceKm = r.DistanceKm,
                District = r.District
            })
            .ToList();

        return new PagedResult<ListingPreviewResponse>
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            HasMore = (page * pageSize) < totalCount,
            Items = items
        };
    }

    public async Task<IReadOnlyList<AiRankCandidate>> GetAiRankCandidatesAsync(
        ListingsQueryFilter filter,
        int limit,
        CancellationToken cancellationToken = default)
    {
        // Same Approved-only chain and default order as the public search, so these are exactly the
        // listings page 1 shows first. Public fields only: no owner, no coordinates, no address.
        var rows = await BuildApprovedListingsQuery(filter)
            .OrderByDescending(listing => listing.CreatedAt)
            .ThenBy(listing => listing.Id)
            .Take(Math.Max(0, limit))
            .Select(listing => new
            {
                listing.Id,
                listing.Title,
                CategoryName = listing.Category.Name,
                listing.PricePerDay,
                listing.AgeFromMonths,
                listing.AgeToMonths,
                listing.Description
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new AiRankCandidate(
                r.Id,
                r.Title,
                r.CategoryName,
                r.PricePerDay,
                r.AgeFromMonths,
                r.AgeToMonths,
                r.Description.Length > 200 ? r.Description[..200] : r.Description))
            .ToList();
    }

    public async Task<ListingMapPinsResponse> GetMapPinsAsync(
        ListingsQueryFilter filter,
        CancellationToken cancellationToken = default)
    {
        // Same filter chain as the paged search, plus an unconditional non-null public-coordinate
        // requirement: a pin cannot be plotted without a point to plot, and per ADR-008 there is no
        // fallback to the exact Latitude/Longitude for any caller here — listings with no public
        // pair are excluded outright, never substituted with a placeholder.
        var query = BuildApprovedListingsQuery(filter)
            .Where(listing => listing.PublicLatitude != null && listing.PublicLongitude != null);

        // Request one row past the cap so truncation can be reported exactly without a second
        // COUNT query.
        var rows = await query
            .OrderByDescending(listing => listing.CreatedAt)
            .Take(MaxMapPins + 1)
            .Select(listing => new
            {
                listing.Id,
                Latitude = listing.PublicLatitude!.Value,
                Longitude = listing.PublicLongitude!.Value,
                listing.Title,
                listing.PricePerDay,
                listing.PriceUnit,
                listing.Currency,
                PrimaryImageUrl = listing.Images
                    .OrderByDescending(image => image.IsPrimary)
                    .ThenBy(image => image.SortOrder)
                    .Select(image => image.Url)
                    .FirstOrDefault(),
                // Same ToyReviews subquery pair as GetApprovedListingsAsync (~line 217-220) —
                // reused verbatim rather than a second, divergent rating formula.
                ReviewCount = _dbContext.ToyReviews.Count(tr => tr.ListingId == listing.Id),
                RatingSum = _dbContext.ToyReviews
                    .Where(tr => tr.ListingId == listing.Id)
                    .Sum(tr => tr.OverallRating)
            })
            .ToListAsync(cancellationToken);

        var isTruncated = rows.Count > MaxMapPins;
        var limitedRows = isTruncated ? rows.Take(MaxMapPins).ToList() : rows;

        var items = limitedRows
            .Select(r => new ListingMapPinResponse
            {
                Id = r.Id,
                Latitude = r.Latitude,
                Longitude = r.Longitude,
                Title = r.Title,
                PricePerDay = r.PricePerDay,
                PriceUnit = r.PriceUnit,
                Currency = r.Currency,
                PrimaryImageUrl = r.PrimaryImageUrl,
                ReviewCount = r.ReviewCount,
                // Aggregate hidden until the minimum number of reviews (2) — same threshold as
                // ListingPreviewResponse.Rating in GetApprovedListingsAsync.
                Rating = r.ReviewCount >= 2 ? Math.Round((decimal)r.RatingSum / r.ReviewCount, 1) : null
            })
            .ToList();

        return new ListingMapPinsResponse
        {
            Items = items,
            IsTruncated = isTruncated
        };
    }

    public async Task<ListingDetailsResponse?> GetApprovedListingByIdAsync(
        Guid id,
        Guid? callerId = null,
        bool isAdmin = false,
        CancellationToken cancellationToken = default)
    {
        var row = await _dbContext.Listings
            .AsNoTracking()
            .Where(listing => listing.Id == id &&
                (isAdmin ||
                 (listing.Status == ListingStatus.Approved && listing.Owner.DeletedAt == null) ||
                 (callerId.HasValue && listing.OwnerId == callerId.Value)))
            .Select(listing => new
            {
                Listing = listing,
                // Single decision point for "may this caller see this listing's exact
                // coordinates?" — owner and admins get the real values; everyone else (incl.
                // anonymous callers) gets the public geohash-cell-centroid pair instead (P1-3,
                // the public-coordinate rule — hotfix H1 first shipped this gate returning null
                // for the false branch, now replaced by PublicLatitude/PublicLongitude below).
                // Every distance/sort computation (the radius filter and DistanceKm in
                // GetApprovedListingsAsync) reads the public pair, never Listing.Latitude/
                // Longitude directly — this is the one seam that decides who gets the exact point.
                CanSeeExactCoordinates = isAdmin || (callerId.HasValue && listing.OwnerId == callerId.Value),
                // Second decision point, gating the pickup AddressLine below: has this caller got a
                // booking on this listing that reached at least Approved? Matches the equivalent
                // gate in BookingDetailResponse — a Pending request must not leak pickup details
                // before the owner accepts.
                ContactRevealed = callerId.HasValue && listing.Bookings.Any(booking =>
                    booking.RenterId == callerId &&
                    (booking.Status == BookingStatus.Approved ||
                     booking.Status == BookingStatus.Active ||
                     booking.Status == BookingStatus.Completed))
            })
            .Select(x => new
            {
                // DeliveryTypes is expanded from this raw flags column in C# below — EF Core
                // cannot translate the flag-expansion loop (DeliveryOptionsMapper.Expand) into SQL,
                // so the column is projected here and the DTO's DeliveryTypes is filled in after
                // materialization (see the `row.Dto.DeliveryTypes = ...` line below).
                DeliveryOptionsRaw = x.Listing.DeliveryOptions,
                Dto = new ListingDetailsResponse
                {
                    Id = x.Listing.Id,
                    Title = x.Listing.Title,
                    Description = x.Listing.Description,
                    PricePerDay = x.Listing.PricePerDay,
                    PriceUnit = x.Listing.PriceUnit,
                    Currency = x.Listing.Currency,
                    Country = x.Listing.Country,
                    City = x.Listing.City,
                    // Pickup address: owner/admin always (they need it to manage the listing), plus a
                    // renter whose booking reached Approved (they genuinely need it to collect the
                    // toy). Everyone else — including anonymous callers and unrelated authenticated
                    // users — gets null. This closes a leak where the exact street address was
                    // returned ungated, defeating the approximate-location feature (only
                    // Latitude/Longitude were gated via CanSeeExactCoordinates).
                    AddressLine = (x.CanSeeExactCoordinates || x.ContactRevealed) ? x.Listing.AddressLine : null,
                    Latitude = x.CanSeeExactCoordinates ? x.Listing.Latitude : x.Listing.PublicLatitude,
                    Longitude = x.CanSeeExactCoordinates ? x.Listing.Longitude : x.Listing.PublicLongitude,
                    District = x.Listing.District == null ? null : new ListingDistrictResponse
                    {
                        Id = x.Listing.District.Id,
                        Code = x.Listing.District.Code,
                        NameEn = x.Listing.District.NameEn,
                        NameHy = x.Listing.District.NameHy,
                        NameRu = x.Listing.District.NameRu
                    },
                    CreatedAt = x.Listing.CreatedAt,
                    UpdatedAt = x.Listing.UpdatedAt,
                    AgeFromMonths = x.Listing.AgeFromMonths,
                    AgeToMonths = x.Listing.AgeToMonths,
                    Condition = x.Listing.Condition,
                    HygieneNotes = x.Listing.HygieneNotes,
                    SafetyNotes = x.Listing.SafetyNotes,
                    CompensationAmount = x.Listing.CompensationAmount,
                    MinRentalDays = x.Listing.MinRentalDays,
                    DeliveryType = x.Listing.DeliveryType,
                    ReviewCount = _dbContext.ToyReviews.Count(tr => tr.ListingId == x.Listing.Id),
                    // Aggregate hidden until the minimum number of reviews (2).
                    Rating = _dbContext.ToyReviews.Count(tr => tr.ListingId == x.Listing.Id) >= 2
                        ? (double?)_dbContext.ToyReviews
                            .Where(tr => tr.ListingId == x.Listing.Id)
                            .Average(tr => (double)tr.OverallRating)
                        : null,
                    Category = new ListingCategoryResponse
                    {
                        Id = x.Listing.Category.Id,
                        Name = x.Listing.Category.Name,
                        NameHy = x.Listing.Category.NameHy,
                        NameRu = x.Listing.Category.NameRu,
                        Slug = x.Listing.Category.Slug
                    },
                    Owner = new ListingOwnerResponse
                    {
                        Id = x.Listing.Owner.Id,
                        FirstName = x.Listing.Owner.DeletedAt == null ? x.Listing.Owner.FirstName : string.Empty,
                        LastName = x.Listing.Owner.DeletedAt == null ? x.Listing.Owner.LastName : string.Empty,
                        AvatarUrl = x.Listing.Owner.DeletedAt == null ? x.Listing.Owner.AvatarUrl : null,
                        IsDeleted = x.Listing.Owner.DeletedAt != null
                    },
                    Images = x.Listing.Images
                        .OrderByDescending(image => image.IsPrimary)
                        .ThenBy(image => image.SortOrder)
                        .Select(image => new ListingImageResponse
                        {
                            Id = image.Id,
                            Url = image.Url,
                            IsPrimary = image.IsPrimary,
                            SortOrder = image.SortOrder
                        })
                        .ToList(),
                    // Both Approved and Active bookings hold the calendar (the booking-create overlap
                    // check blocks all three of Pending/Approved/Active), so the public calendar must
                    // surface Active ranges too — otherwise a date shows free but the request 409s.
                    BookedDateRanges = x.Listing.Bookings
                        .Where(booking => booking.Status == BookingStatus.Approved ||
                                          booking.Status == BookingStatus.Active)
                        .OrderBy(booking => booking.StartDate)
                        .Select(booking => new ListingBookedDateRangeResponse
                        {
                            StartDate = booking.StartDate,
                            EndDate = booking.EndDate
                        })
                        .ToList()
                }
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        row.Dto.DeliveryTypes = DeliveryOptionsMapper.Expand(row.DeliveryOptionsRaw, row.Dto.DeliveryType);
        return row.Dto;
    }
}
