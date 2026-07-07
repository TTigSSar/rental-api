using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;

namespace RentalPlatform.Infrastructure.Services;

public sealed class PublicUserProfileQueryService : IPublicUserProfileService
{
    private readonly AppDbContext _dbContext;
    private readonly IReviewsStore _reviewsStore;

    public PublicUserProfileQueryService(AppDbContext dbContext, IReviewsStore reviewsStore)
    {
        _dbContext = dbContext;
        _reviewsStore = reviewsStore;
    }

    public async Task<PublicUserProfileResponse?> GetPublicProfileAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var user = await _dbContext.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new
            {
                u.Id,
                u.FirstName,
                u.LastName,
                u.AvatarUrl,
                u.CreatedAt,
                u.Location,
                u.IsEmailConfirmed,
                u.IsPhoneConfirmed,
                u.IsIdConfirmed,
                u.PhoneNumber,
                ActiveListingsCount = u.Listings.Count(l => l.Status == ListingStatus.Approved)
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (user is null) return null;

        // Lightweight DB-side aggregates: the profile only needs counts + overall averages,
        // not the full comment cards the review-summary endpoints build.
        var ownerRating = await _reviewsStore.GetOwnerRatingAggregateAsync(userId, cancellationToken);
        var renterRating = await _reviewsStore.GetRenterRatingAggregateAsync(userId, cancellationToken);
        var ownerHasAggregate = ownerRating.Count >= ReviewsService.MinReviewsForAggregate;
        var renterHasAggregate = renterRating.Count >= ReviewsService.MinReviewsForAggregate;

        var completedAsOwner = await _dbContext.Bookings
            .AsNoTracking()
            .CountAsync(b => b.Listing.OwnerId == userId && b.Status == BookingStatus.Completed, cancellationToken);

        var completedAsRenter = await _dbContext.Bookings
            .AsNoTracking()
            .CountAsync(b => b.RenterId == userId && b.Status == BookingStatus.Completed, cancellationToken);

        var isEmailPhoneConfirmed = user.IsEmailConfirmed
            && user.PhoneNumber != null
            && user.IsPhoneConfirmed;

        return new PublicUserProfileResponse
        {
            Id = user.Id,
            FirstName = user.FirstName,
            LastName = user.LastName,
            AvatarUrl = user.AvatarUrl,
            MemberSince = user.CreatedAt,
            ActiveListingsCount = user.ActiveListingsCount,

            // Legacy flat rating — keep backward-compat
            AverageRating = ownerHasAggregate ? ownerRating.Average : 0,
            ReviewCount = ownerRating.Count,

            // Trust surface
            Location = user.Location,
            IsVerified = user.IsEmailConfirmed && user.IsPhoneConfirmed,
            IsIdConfirmed = user.IsIdConfirmed,
            IsEmailPhoneConfirmed = isEmailPhoneConfirmed,

            // As Owner
            OwnerRating = ownerHasAggregate ? ownerRating.Average : null,
            OwnerReviewCount = ownerRating.Count,
            CompletedRentalsAsOwner = completedAsOwner,
            ResponseRate = null,
            HygieneScore = null,
            HygieneStandards = [],

            // As Renter
            RenterRating = renterHasAggregate ? renterRating.Average : null,
            RenterReviewCount = renterRating.Count,
            CompletedRentalsAsRenter = completedAsRenter,
            OnTimeReturnRate = null,
            DamageClaims = 0,
            ReliabilityMetrics = []
        };
    }

    public async Task<IReadOnlyList<ListingPreviewResponse>> GetUserListingsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var rows = await _dbContext.Listings
            .AsNoTracking()
            .Where(l => l.OwnerId == userId && l.Status == ListingStatus.Approved)
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => new
            {
                l.Id,
                l.CategoryId,
                CategoryName = l.Category.Name,
                l.Title,
                l.PricePerDay,
                l.PriceUnit,
                l.Currency,
                l.Country,
                l.City,
                PrimaryImageUrl = l.Images
                    .OrderByDescending(i => i.IsPrimary)
                    .ThenBy(i => i.SortOrder)
                    .Select(i => i.Url)
                    .FirstOrDefault(),
                l.AgeFromMonths,
                l.AgeToMonths,
                l.Condition,
                l.CreatedAt,
                ReviewCount = _dbContext.ToyReviews.Count(tr => tr.ListingId == l.Id),
                RatingSum = _dbContext.ToyReviews
                    .Where(tr => tr.ListingId == l.Id)
                    .Sum(tr => tr.OverallRating)
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
                Rating = r.ReviewCount >= 2 ? Math.Round((double)r.RatingSum / r.ReviewCount, 1) : null
            })
            .ToList();
    }
}
