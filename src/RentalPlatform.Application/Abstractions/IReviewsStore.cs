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
}
