using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;

namespace RentalPlatform.Infrastructure.Services;

public sealed class HomeSectionsQueryService : IHomeSectionsService
{
    private const int MaxItemsPerSection = 12;

    private readonly AppDbContext _dbContext;

    public HomeSectionsQueryService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<HomeSectionsResponse> GetSectionsAsync(
        int itemsPerSection,
        CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(itemsPerSection, 1, MaxItemsPerSection);

        // Popular: approved listings ordered by total number of bookings, then newest first.
        var popular = await Approved()
            .OrderByDescending(listing => listing.Bookings.Count)
            .ThenByDescending(listing => listing.CreatedAt)
            .Take(limit)
            .Select(ToPreview)
            .ToListAsync(cancellationToken);

        // Recent: newest approved listings first.
        var recent = await Approved()
            .OrderByDescending(listing => listing.CreatedAt)
            .Take(limit)
            .Select(ToPreview)
            .ToListAsync(cancellationToken);

        // Liked: approved listings ordered by number of times favorited, then newest first.
        var liked = await Approved()
            .OrderByDescending(listing => listing.Favorites.Count)
            .ThenByDescending(listing => listing.CreatedAt)
            .Take(limit)
            .Select(ToPreview)
            .ToListAsync(cancellationToken);

        // Category sections: ordered by newest first; empty list when category has no approved listings.
        var babyToys = await Approved()
            .Where(listing => listing.Category.Slug == "baby-toys")
            .OrderByDescending(listing => listing.CreatedAt)
            .Take(limit)
            .Select(ToPreview)
            .ToListAsync(cancellationToken);

        var outdoorToys = await Approved()
            .Where(listing => listing.Category.Slug == "outdoor-toys")
            .OrderByDescending(listing => listing.CreatedAt)
            .Take(limit)
            .Select(ToPreview)
            .ToListAsync(cancellationToken);

        return new HomeSectionsResponse
        {
            Sections =
            [
                new HomeSectionResponse { Key = "recent",       Title = "Most Recent Toys", Items = recent },
                new HomeSectionResponse { Key = "popular",      Title = "Popular Toys",     Items = popular },
                new HomeSectionResponse { Key = "liked",        Title = "Most Liked Toys",  Items = liked },
                new HomeSectionResponse { Key = "baby-toys",    Title = "Baby Toys",        Items = babyToys },
                new HomeSectionResponse { Key = "outdoor-toys", Title = "Outdoor Toys",     Items = outdoorToys },
            ]
        };
    }

    // Base query shared by all sections: only public approved listings, no tracking.
    private IQueryable<Listing> Approved() =>
        _dbContext.Listings
            .AsNoTracking()
            .Where(listing => listing.Status == ListingStatus.Approved);

    // Shared projection reused across all section queries.
    private static readonly Expression<Func<Listing, ListingPreviewResponse>> ToPreview =
        listing => new ListingPreviewResponse
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
        };
}
