using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Abstractions;

public interface IReviewsStore
{
    Task AddAsync(Review review, CancellationToken cancellationToken = default);
    Task<bool> HasReviewForBookingAsync(Guid bookingId, ReviewerRole role, CancellationToken cancellationToken = default);
    Task<Booking?> FindBookingForReviewAsync(Guid bookingId, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<Review>> GetByListingAsync(Guid listingId, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<Review>> GetByUserAsync(Guid userId, CancellationToken cancellationToken = default);
}
