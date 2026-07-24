using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
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

    // Maps P2-1: cap on map-pin results. IsTruncated is derived by requesting one extra row and
    // checking whether it came back, so truncation is exact without a second COUNT query.
    private const int MaxMapPins = 500;

    private readonly AppDbContext _dbContext;

    public ListingsQueryService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // Shared predicate chain for both the paged public search and the map-pins endpoint — they
    // must never diverge on what "an approved, publicly-visible listing matching this filter"
    // means; only the projection/shape after this differs.
    private IQueryable<Domain.Entities.Listing> BuildApprovedListingsQuery(ListingsQueryFilter filter)
    {
        var query = _dbContext.Listings
            .AsNoTracking()
            .Where(listing => listing.Status == ListingStatus.Approved);

        if (!string.IsNullOrWhiteSpace(filter.City))
        {
            var city = filter.City.Trim();
            query = query.Where(listing => listing.City == city);
        }

        if (filter.CategoryId.HasValue)
        {
            query = query.Where(listing => listing.CategoryId == filter.CategoryId.Value);
        }

        if (filter.DistrictIds is { Count: > 0 })
        {
            var districtIds = filter.DistrictIds.Distinct().Take(MaxDistrictIds).ToList();
            query = query.Where(listing => listing.DistrictId != null && districtIds.Contains(listing.DistrictId.Value));
        }

        if (filter.MinPrice.HasValue)
        {
            query = query.Where(listing => listing.PricePerDay >= filter.MinPrice.Value);
        }

        if (filter.MaxPrice.HasValue)
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

        if (filter.OriginLat.HasValue && filter.OriginLng.HasValue && filter.RadiusKm.HasValue)
        {
            // Bounding-box approximation over the PUBLIC coordinates only (never the exact
            // Latitude/Longitude — see the public-coordinate rule at P1-3/GetApprovedListingByIdAsync
            // above). A square box, not a Haversine circle: the ~1.2km geohash-cell snapping already
            // introduces slack of that order, so a tighter circular refinement is a documented
            // follow-up (Maps P2-1 circular refinement) rather than in scope here.
            var radiusKm = filter.RadiusKm.Value;
            var originLat = filter.OriginLat.Value;
            var originLng = filter.OriginLng.Value;

            var dLat = radiusKm / 111.0;
            var dLng = radiusKm / (111.0 * Math.Cos((double)originLat * Math.PI / 180.0));

            var minLat = originLat - (decimal)dLat;
            var maxLat = originLat + (decimal)dLat;
            var minLng = originLng - (decimal)dLng;
            var maxLng = originLng + (decimal)dLng;

            query = ApplyBoundingBox(query, minLat, maxLat, minLng, maxLng);
        }

        if (filter.MinLat.HasValue && filter.MaxLat.HasValue && filter.MinLng.HasValue && filter.MaxLng.HasValue)
        {
            // Maps P2-1 viewport search: same public-coordinate box predicate as the radius filter
            // above, just with caller-supplied bounds instead of ones derived from origin+radius.
            query = ApplyBoundingBox(query, filter.MinLat.Value, filter.MaxLat.Value, filter.MinLng.Value, filter.MaxLng.Value);
        }

        return query;
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

    public async Task<PagedResult<ListingPreviewResponse>> GetApprovedListingsAsync(
        ListingsQueryFilter filter,
        CancellationToken cancellationToken = default)
    {
        var page = filter.Page < 1 ? DefaultPage : filter.Page;
        var pageSize = filter.PageSize < 1 ? DefaultPageSize : Math.Min(filter.PageSize, MaxPageSize);

        var query = BuildApprovedListingsQuery(filter);

        var totalCount = await query.CountAsync(cancellationToken);

        var rows = await query
            .OrderByDescending(listing => listing.CreatedAt)
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
                    .Sum(tr => tr.OverallRating)
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
                Rating = r.ReviewCount >= 2 ? Math.Round((double)r.RatingSum / r.ReviewCount, 1) : null
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
            .Select(listing => new ListingMapPinResponse
            {
                Id = listing.Id,
                Latitude = listing.PublicLatitude!.Value,
                Longitude = listing.PublicLongitude!.Value,
                Title = listing.Title,
                PricePerDay = listing.PricePerDay,
                PriceUnit = listing.PriceUnit,
                Currency = listing.Currency,
                PrimaryImageUrl = listing.Images
                    .OrderByDescending(image => image.IsPrimary)
                    .ThenBy(image => image.SortOrder)
                    .Select(image => image.Url)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        var isTruncated = rows.Count > MaxMapPins;
        var items = isTruncated ? rows.Take(MaxMapPins).ToList() : rows;

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
        return await _dbContext.Listings
            .AsNoTracking()
            .Where(listing => listing.Id == id &&
                (isAdmin ||
                 listing.Status == ListingStatus.Approved ||
                 (callerId.HasValue && listing.OwnerId == callerId.Value)))
            .Select(listing => new
            {
                Listing = listing,
                // Single decision point for "may this caller see this listing's exact
                // coordinates?" — owner and admins get the real values; everyone else (incl.
                // anonymous callers) gets the public geohash-cell-centroid pair instead (P1-3,
                // the public-coordinate rule — hotfix H1 first shipped this gate returning null
                // for the false branch, now replaced by PublicLatitude/PublicLongitude below).
                // Any future distance/sort computation (Phase 2, not implemented yet) MUST read
                // the public pair, never Listing.Latitude/Longitude directly, and round its own
                // output — this is the one seam that decides who gets the exact point.
                CanSeeExactCoordinates = isAdmin || (callerId.HasValue && listing.OwnerId == callerId.Value),
                // Second decision point, reused below for both the owner's phone number and the
                // pickup AddressLine: has this caller got a booking on this listing that reached
                // at least Approved? Matches the contact-reveal gate in BookingDetailResponse —
                // a Pending request must not leak contact/pickup details before the owner accepts.
                ContactRevealed = callerId.HasValue && listing.Bookings.Any(booking =>
                    booking.RenterId == callerId &&
                    (booking.Status == BookingStatus.Approved ||
                     booking.Status == BookingStatus.Active ||
                     booking.Status == BookingStatus.Completed))
            })
            .Select(x => new ListingDetailsResponse
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
                DepositAmount = x.Listing.DepositAmount,
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
                    Slug = x.Listing.Category.Slug
                },
                Owner = new ListingOwnerResponse
                {
                    Id = x.Listing.Owner.Id,
                    FirstName = x.Listing.Owner.FirstName,
                    LastName = x.Listing.Owner.LastName,
                    AvatarUrl = x.Listing.Owner.AvatarUrl,
                    // Reveal the owner's phone only once the renter has a booking that reached at
                    // least Approved — matching the contact-reveal gate in BookingDetail. A Pending
                    // request must NOT expose contact details before the owner has accepted it.
                    PhoneNumber = x.ContactRevealed ? x.Listing.Owner.PhoneNumber : null
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
            })
            .FirstOrDefaultAsync(cancellationToken);
    }
}
