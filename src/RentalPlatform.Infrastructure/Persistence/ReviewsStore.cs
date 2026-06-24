using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Persistence;

public sealed class ReviewsStore : IReviewsStore
{
    private readonly AppDbContext _dbContext;

    public ReviewsStore(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<Booking?> FindBookingForReviewAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        _dbContext.Bookings
            .Include(b => b.Listing)
            .FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken);

    public Task<bool> HasToyReviewAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        _dbContext.ToyReviews.AnyAsync(r => r.BookingId == bookingId, cancellationToken);

    public Task<bool> HasOwnerReviewAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        _dbContext.OwnerReviews.AnyAsync(r => r.BookingId == bookingId, cancellationToken);

    public Task<bool> HasRenterReviewAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        _dbContext.RenterReviews.AnyAsync(r => r.BookingId == bookingId, cancellationToken);

    public async Task AddToyReviewAsync(ToyReview review, CancellationToken cancellationToken = default)
    {
        await _dbContext.ToyReviews.AddAsync(review, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task AddOwnerReviewAsync(OwnerReview review, CancellationToken cancellationToken = default)
    {
        await _dbContext.OwnerReviews.AddAsync(review, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task AddRenterReviewAsync(RenterReview review, CancellationToken cancellationToken = default)
    {
        await _dbContext.RenterReviews.AddAsync(review, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ToyReview>> GetToyReviewsByListingAsync(
        Guid listingId,
        CancellationToken cancellationToken = default) =>
        await _dbContext.ToyReviews
            .AsNoTracking()
            .Where(r => r.ListingId == listingId)
            .Include(r => r.Reviewer)
            .Include(r => r.Booking)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<OwnerReview>> GetOwnerReviewsByUserAsync(
        Guid ownerId,
        CancellationToken cancellationToken = default) =>
        await _dbContext.OwnerReviews
            .AsNoTracking()
            .Where(r => r.OwnerId == ownerId)
            .Include(r => r.Reviewer)
            .Include(r => r.Booking)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<RenterReview>> GetRenterReviewsByUserAsync(
        Guid renterId,
        CancellationToken cancellationToken = default) =>
        await _dbContext.RenterReviews
            .AsNoTracking()
            .Where(r => r.RenterId == renterId)
            .Include(r => r.Reviewer)
            .Include(r => r.Booking)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<RatingAggregate> GetOwnerRatingAggregateAsync(
        Guid ownerId,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.OwnerReviews.AsNoTracking().Where(r => r.OwnerId == ownerId);
        var count = await query.CountAsync(cancellationToken);
        if (count == 0)
        {
            return new RatingAggregate(0, 0.0);
        }

        // Mirrors ReviewsService owner overall = avg((Communication + PickupHandover + Friendliness) / 3).
        var average = await query.AverageAsync(
            r => (r.CommunicationRating + r.PickupHandoverRating + r.FriendlinessRating) / 3.0,
            cancellationToken);

        return new RatingAggregate(count, Math.Round(average, 1));
    }

    public async Task<RatingAggregate> GetRenterRatingAggregateAsync(
        Guid renterId,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.RenterReviews.AsNoTracking().Where(r => r.RenterId == renterId);
        var count = await query.CountAsync(cancellationToken);
        if (count == 0)
        {
            return new RatingAggregate(0, 0.0);
        }

        // Mirrors ReviewsService renter overall = avg((Communication + ReturnedOnTime + CareOfToy + WouldRentAgain) / 4).
        var average = await query.AverageAsync(
            r => (r.CommunicationRating + r.ReturnedOnTimeRating + r.CareOfToyRating + r.WouldRentAgainRating) / 4.0,
            cancellationToken);

        return new RatingAggregate(count, Math.Round(average, 1));
    }
}
