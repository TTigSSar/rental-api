using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Abstractions;

public interface IBookingsStore
{
    Task ExpirePendingAsync(DateTime utcNow, CancellationToken cancellationToken = default);

    // Auto-completes owner-initiated returns whose 48h confirmation window has elapsed.
    // Renter-initiated returns are intentionally never auto-completed.
    Task CompleteOverdueReturnsAsync(DateTime utcNow, CancellationToken cancellationToken = default);
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

    // Loads a booking with everything the Booking Details page needs: listing, its owner,
    // category and images, plus the renter — so either party's view can be projected.
    Task<Booking?> FindBookingDetailByIdAsync(Guid bookingId, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
