using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Abstractions;

public enum ApproveBookingOutcome
{
    Approved,
    Overlap,
    NotPending
}

public interface IBookingsStore
{
    Task<IReadOnlyList<Booking>> ExpirePendingAsync(DateTime utcNow, CancellationToken cancellationToken = default);
    Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<Listing?> FindListingByIdAsync(Guid listingId, CancellationToken cancellationToken = default);

    // Fresh, untracked read: true when the listing's owner account has been deleted (ADR-056).
    Task<bool> IsListingOwnerDeletedAsync(Guid listingId, CancellationToken cancellationToken = default);

    // Atomically checks for Pending+Approved+Active overlap and, if absent, persists the booking.
    // Returns true when the booking was added; false when a blocking overlap was detected or, inside the same
    // transaction, the listing's owner turned out to be deleted (ADR-056 amendment 6: the read blocks on a deletion
    // in progress, which holds the owner's row lock until it commits).
    // Implemented under a SERIALIZABLE transaction so concurrent creates cannot both succeed.
    /// <summary>
    /// Atomic overlap-check, insert and demand-counter increment (P3-3). <paramref name="districtId"/> and
    /// <paramref name="categoryId"/> are the listing's values at request time, passed by the caller so the
    /// store takes no Listings lock inside its transaction before the Users read.
    /// </summary>
    Task<bool> TryCreateBookingAsync(
        Booking booking,
        Guid? districtId,
        Guid categoryId,
        CancellationToken cancellationToken = default);

    // Atomically re-checks for an Approved/Active overlap (excluding this booking) and, if absent,
    // approves the booking by a compare-and-set on Status = Pending (ApprovedAt/UpdatedAt are taken from the
    // tracked booking the caller already mutated). Overlap when a blocking overlap exists; NotPending when the
    // booking left Pending in the meantime (e.g. an account deletion cancelled it, ADR-056), in which case nothing
    // is written. Implemented under a SERIALIZABLE transaction so two concurrent approvals of overlapping
    // pending requests on the same listing cannot both succeed (double-booking guard).
    Task<ApproveBookingOutcome> TryApproveBookingAsync(Booking booking, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<Booking>> GetRenterBookingsAsync(Guid renterId, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<Booking>> GetOwnerBookingRequestsAsync(Guid ownerId, CancellationToken cancellationToken = default);
    Task<Booking?> FindBookingWithRelationsByIdAsync(Guid bookingId, CancellationToken cancellationToken = default);

    // Loads a booking with everything the Booking Details page needs: listing, its owner,
    // category and images, plus the renter — so either party's view can be projected.
    Task<Booking?> FindBookingDetailByIdAsync(Guid bookingId, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
