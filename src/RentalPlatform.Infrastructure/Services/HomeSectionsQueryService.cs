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
        var popular = await ToPreviewsAsync(Approved()
            .OrderByDescending(listing => listing.Bookings.Count)
            .ThenByDescending(listing => listing.CreatedAt)
            .Take(limit), cancellationToken);

        // Recent: newest approved listings first.
        var recent = await ToPreviewsAsync(Approved()
            .OrderByDescending(listing => listing.CreatedAt)
            .Take(limit), cancellationToken);

        // Liked: approved listings ordered by number of times favorited, then newest first.
        var liked = await ToPreviewsAsync(Approved()
            .OrderByDescending(listing => listing.Favorites.Count)
            .ThenByDescending(listing => listing.CreatedAt)
            .Take(limit), cancellationToken);

        // Category sections: ordered by newest first; empty list when category has no approved listings.
        var babyToys = await ToPreviewsAsync(Approved()
            .Where(listing => listing.Category.Slug == "baby-toys")
            .OrderByDescending(listing => listing.CreatedAt)
            .Take(limit), cancellationToken);

        var outdoorToys = await ToPreviewsAsync(Approved()
            .Where(listing => listing.Category.Slug == "outdoor-toys")
            .OrderByDescending(listing => listing.CreatedAt)
            .Take(limit), cancellationToken);

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

    // Base query shared by all sections: only public approved listings, no tracking. Admin console
    // Phase 2 was narrowed by human review — hiding a category retires the LABEL (excluded from
    // GET /api/categories), not the inventory. The home page carries no category filter (even the
    // two hardcoded slug carousels below are not the "explicit category filter" path the narrowing
    // applies to — see ListingsQueryService.BuildApprovedListingsQuery), so a hidden category's
    // listings appear here same as any other approved listing.
    private IQueryable<Listing> Approved() =>
        _dbContext.Listings
            .AsNoTracking()
            // ADR-056: see ListingsQueryService.
            .Where(listing => listing.Status == ListingStatus.Approved && listing.Owner.DeletedAt == null);

    // Shared projection reused across all section queries. Rating/ReviewCount follow the catalogue
    // rule (ListingsQueryService): the aggregate is hidden below 2 reviews. The rounding happens in
    // memory after the single SQL round trip, same as the catalogue.
    private async Task<List<ListingPreviewResponse>> ToPreviewsAsync(
        IQueryable<Listing> query,
        CancellationToken cancellationToken)
    {
        var rows = await query
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
                // Districts are public and coarse (ADR-008/024); no coordinates are projected.
                District = listing.District == null ? null : new ListingDistrictResponse
                {
                    Id = listing.District.Id,
                    Code = listing.District.Code,
                    NameEn = listing.District.NameEn,
                    NameHy = listing.District.NameHy,
                    NameRu = listing.District.NameRu
                }
            })
            .ToListAsync(cancellationToken);

        return rows
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
                Rating = r.ReviewCount >= 2 ? Math.Round((double)r.RatingSum / r.ReviewCount, 1) : null,
                District = r.District
            })
            .ToList();
    }
}
