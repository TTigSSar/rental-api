using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Abstractions;

public interface IBookingsStore
{
    Task ExpirePendingAsync(DateTime utcNow, CancellationToken cancellationToken = default);
    Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<Listing?> FindListingByIdAsync(Guid listingId, CancellationToken cancellationToken = default);

    // Atomically checks for Pending+Approved+Active overlap and, if absent, persists the booking.
    // Returns true when the booking was added; false when a blocking overlap was detected.
    // Implemented under a SERIALIZABLE transaction so concurrent creates cannot both succeed.
    Task<bool> TryCreateBookingAsync(Booking booking, CancellationToken cancellationToken = default);

    // Atomically re-checks for an Approved/Active overlap (excluding this booking) and, if absent,
    // persists the already-mutated tracked booking. Returns false when a blocking overlap exists.
    // Implemented under a SERIALIZABLE transaction so two concurrent approvals of overlapping
    // pending requests on the same listing cannot both succeed (double-booking guard).
    Task<bool> TryApproveBookingAsync(Booking booking, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<Booking>> GetRenterBookingsAsync(Guid renterId, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<Booking>> GetOwnerBookingRequestsAsync(Guid ownerId, CancellationToken cancellationToken = default);
    Task<Booking?> FindBookingWithRelationsByIdAsync(Guid bookingId, CancellationToken cancellationToken = default);

    // Loads a booking with everything the Booking Details page needs: listing, its owner,
    // category and images, plus the renter — so either party's view can be projected.
    Task<Booking?> FindBookingDetailByIdAsync(Guid bookingId, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
