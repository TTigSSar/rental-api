using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Abstractions;

public interface IBookingsStore
{
    Task ExpirePendingAsync(DateTime utcNow, CancellationToken cancellationToken = default);
    Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<Listing?> FindListingByIdAsync(Guid listingId, CancellationToken cancellationToken = default);
    Task<bool> HasApprovedOverlapAsync(
        Guid listingId,
        DateOnly startDate,
        DateOnly endDate,
        Guid? excludedBookingId,
        CancellationToken cancellationToken = default);

    // Atomically checks for Pending+Approved overlap and, if absent, persists the booking.
    // Returns true when the booking was added; false when a blocking overlap was detected.
    // Implemented under a SERIALIZABLE transaction so concurrent creates cannot both succeed.
    Task<bool> TryCreateBookingAsync(Booking booking, CancellationToken cancellationToken = default);

    Task AddBookingAsync(Booking booking, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<Booking>> GetRenterBookingsAsync(Guid renterId, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<Booking>> GetOwnerBookingRequestsAsync(Guid ownerId, CancellationToken cancellationToken = default);
    Task<Booking?> FindBookingWithRelationsByIdAsync(Guid bookingId, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
