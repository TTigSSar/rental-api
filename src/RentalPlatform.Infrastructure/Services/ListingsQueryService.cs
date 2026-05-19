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

        var items = await query
            .OrderByDescending(listing => listing.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(listing => new ListingPreviewResponse
            {
                Id = listing.Id,
                CategoryId = listing.CategoryId,
                CategoryName = listing.Category.Name,
                Title = listing.Title,
                PricePerDay = listing.PricePerDay,
                Currency = listing.Currency,
                Country = listing.Country,
                City = listing.City,
                PrimaryImageUrl = listing.Images
                    .OrderByDescending(image => image.IsPrimary)
                    .ThenBy(image => image.SortOrder)
                    .Select(image => image.Url)
                    .FirstOrDefault(),
                AgeFromMonths = listing.AgeFromMonths,
                AgeToMonths = listing.AgeToMonths,
                Condition = listing.Condition,
                CreatedAt = listing.CreatedAt
            })
            .ToListAsync(cancellationToken);

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
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Listings
            .AsNoTracking()
            .Where(listing => listing.Id == id && listing.Status == ListingStatus.Approved)
            .Select(listing => new ListingDetailsResponse
            {
                Id = listing.Id,
                Title = listing.Title,
                Description = listing.Description,
                PricePerDay = listing.PricePerDay,
                Currency = listing.Currency,
                Country = listing.Country,
                City = listing.City,
                AddressLine = listing.AddressLine,
                Latitude = listing.Latitude,
                Longitude = listing.Longitude,
                CreatedAt = listing.CreatedAt,
                UpdatedAt = listing.UpdatedAt,
                AgeFromMonths = listing.AgeFromMonths,
                AgeToMonths = listing.AgeToMonths,
                Condition = listing.Condition,
                HygieneNotes = listing.HygieneNotes,
                SafetyNotes = listing.SafetyNotes,
                DepositAmount = listing.DepositAmount,
                Category = new ListingCategoryResponse
                {
                    Id = listing.Category.Id,
                    Name = listing.Category.Name,
                    Slug = listing.Category.Slug
                },
                Owner = new ListingOwnerResponse
                {
                    Id = listing.Owner.Id,
                    FirstName = listing.Owner.FirstName,
                    LastName = listing.Owner.LastName,
                    AvatarUrl = listing.Owner.AvatarUrl
                },
                Images = listing.Images
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
                BookedDateRanges = listing.Bookings
                    .Where(booking => booking.Status == BookingStatus.Approved)
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
