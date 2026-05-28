using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Infrastructure.Persistence;

public sealed class ReviewsStore : IReviewsStore
{
    private readonly AppDbContext _dbContext;

    public ReviewsStore(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(Review review, CancellationToken cancellationToken = default)
    {
        await _dbContext.Reviews.AddAsync(review, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task<bool> HasReviewForBookingAsync(
        Guid bookingId,
        ReviewerRole role,
        CancellationToken cancellationToken = default) =>
        _dbContext.Reviews.AnyAsync(
            r => r.BookingId == bookingId && r.ReviewerRole == role,
            cancellationToken);

    public Task<Booking?> FindBookingForReviewAsync(
        Guid bookingId,
        CancellationToken cancellationToken = default) =>
        _dbContext.Bookings
            .Include(b => b.Listing)
            .FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken);

    public Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

    public async Task<IReadOnlyCollection<Review>> GetByListingAsync(
        Guid listingId,
        CancellationToken cancellationToken = default) =>
        await _dbContext.Reviews
            .AsNoTracking()
            .Where(r => r.ListingId == listingId && r.ReviewerRole == ReviewerRole.Renter)
            .Include(r => r.Reviewer)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyCollection<Review>> GetByUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        await _dbContext.Reviews
            .AsNoTracking()
            .Where(r => r.RevieweeId == userId)
            .Include(r => r.Reviewer)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<(int Count, double AverageRating)> GetListingSummaryAsync(
        Guid listingId,
        CancellationToken cancellationToken = default)
    {
        // Only renter reviews count toward the listing rating.
        var query = _dbContext.Reviews
            .Where(r => r.ListingId == listingId && r.ReviewerRole == ReviewerRole.Renter);

        var count = await query.CountAsync(cancellationToken);
        if (count == 0) return (0, 0.0);

        var avg = await query.AverageAsync(r => (double)r.Rating, cancellationToken);
        return (count, Math.Round(avg, 1));
    }

    public async Task<(int Count, double AverageRating)> GetUserSummaryAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // All reviews received by this user (as owner or as renter).
        var query = _dbContext.Reviews.Where(r => r.RevieweeId == userId);

        var count = await query.CountAsync(cancellationToken);
        if (count == 0) return (0, 0.0);

        var avg = await query.AverageAsync(r => (double)r.Rating, cancellationToken);
        return (count, Math.Round(avg, 1));
    }
}
