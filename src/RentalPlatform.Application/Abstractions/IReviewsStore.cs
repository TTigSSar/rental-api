using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Abstractions;

public interface IReviewsStore
{
    Task<Booking?> FindBookingForReviewAsync(Guid bookingId, CancellationToken cancellationToken = default);

    Task<bool> HasToyReviewAsync(Guid bookingId, CancellationToken cancellationToken = default);
    Task<bool> HasOwnerReviewAsync(Guid bookingId, CancellationToken cancellationToken = default);
    Task<bool> HasRenterReviewAsync(Guid bookingId, CancellationToken cancellationToken = default);

    Task AddToyReviewAsync(ToyReview review, CancellationToken cancellationToken = default);
    Task AddOwnerReviewAsync(OwnerReview review, CancellationToken cancellationToken = default);
    Task AddRenterReviewAsync(RenterReview review, CancellationToken cancellationToken = default);

    // Read sides for aggregation. Reviewer + Booking are included so the service
    // can build public comment cards (name, avatar, rented days).
    Task<IReadOnlyList<ToyReview>> GetToyReviewsByListingAsync(Guid listingId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OwnerReview>> GetOwnerReviewsByUserAsync(Guid ownerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RenterReview>> GetRenterReviewsByUserAsync(Guid renterId, CancellationToken cancellationToken = default);

    // Lightweight count + overall-average aggregates computed in the database, without loading
    // rows, joins, or comment cards — used by surfaces (e.g. public profiles) that only need the
    // numbers. The averages mirror the composite formulas in ReviewsService.
    Task<RatingAggregate> GetOwnerRatingAggregateAsync(Guid ownerId, CancellationToken cancellationToken = default);
    Task<RatingAggregate> GetRenterRatingAggregateAsync(Guid renterId, CancellationToken cancellationToken = default);
}

// Count of reviews and their rounded overall average (0 when there are none).
public sealed record RatingAggregate(int Count, double Average);
