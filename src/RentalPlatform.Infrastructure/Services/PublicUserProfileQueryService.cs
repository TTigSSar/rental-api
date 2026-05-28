using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.DTOs;
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

        var (reviewCount, averageRating) = await _reviewsStore.GetUserSummaryAsync(userId, cancellationToken);

        return new PublicUserProfileResponse
        {
            Id = user.Id,
            FirstName = user.FirstName,
            LastName = user.LastName,
            AvatarUrl = user.AvatarUrl,
            MemberSince = user.CreatedAt,
            ActiveListingsCount = user.ActiveListingsCount,
            AverageRating = averageRating,
            ReviewCount = reviewCount
        };
    }
}
