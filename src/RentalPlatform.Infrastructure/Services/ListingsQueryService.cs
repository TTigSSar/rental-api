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

    private readonly AppDbContext _dbContext;

    public ListingsQueryService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<PagedResult<ListingPreviewResponse>> GetApprovedListingsAsync(
        ListingsQueryFilter filter,
        CancellationToken cancellationToken = default)
    {
        var page = filter.Page < 1 ? DefaultPage : filter.Page;
        var pageSize = filter.PageSize < 1 ? DefaultPageSize : Math.Min(filter.PageSize, MaxPageSize);

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

        if (filter.MinPrice.HasValue)
        {
            query = query.Where(listing => listing.PricePerDay >= filter.MinPrice.Value);
        }

        if (filter.MaxPrice.HasValue)
        {
            query = query.Where(listing => listing.PricePerDay <= filter.MaxPrice.Value);
        }

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
                CanSeeExactCoordinates = isAdmin || (callerId.HasValue && listing.OwnerId == callerId.Value)
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
                AddressLine = x.Listing.AddressLine,
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
                    PhoneNumber = callerId != null && x.Listing.Bookings.Any(booking =>
                        booking.RenterId == callerId &&
                        (booking.Status == BookingStatus.Approved ||
                         booking.Status == BookingStatus.Active ||
                         booking.Status == BookingStatus.Completed))
                        ? x.Listing.Owner.PhoneNumber
                        : null
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
