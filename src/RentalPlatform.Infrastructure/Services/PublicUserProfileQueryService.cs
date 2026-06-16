using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;

namespace RentalPlatform.Infrastructure.Services;

public sealed class PublicUserProfileQueryService : IPublicUserProfileService
{
    private readonly AppDbContext _dbContext;
    private readonly IReviewsService _reviewsService;

    public PublicUserProfileQueryService(AppDbContext dbContext, IReviewsService reviewsService)
    {
        _dbContext = dbContext;
        _reviewsService = reviewsService;
    }

    public async Task<PublicUserProfileResponse?> GetPublicProfileAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Single query: user fields + approved listing count via nav property.
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
                ActiveListingsCount = u.Listings.Count(l => l.Status == ListingStatus.Approved)
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (user is null) return null;

        // A public profile reflects the user's reputation as an owner.
        var ownerReviews = await _reviewsService.GetOwnerReviewsAsync(userId, cancellationToken);

        return new PublicUserProfileResponse
        {
            Id = user.Id,
            FirstName = user.FirstName,
            LastName = user.LastName,
            AvatarUrl = user.AvatarUrl,
            MemberSince = user.CreatedAt,
            ActiveListingsCount = user.ActiveListingsCount,
            AverageRating = ownerReviews.HasAggregate ? ownerReviews.OverallAverage : 0,
            ReviewCount = ownerReviews.ReviewCount
        };
    }
}
