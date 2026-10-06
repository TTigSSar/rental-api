using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Abstractions;

// Backs HomePointService — the single writer for User.Home*/Listing location fields (home-point model).
public interface IHomePointStore
{
    Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<District?> FindDistrictByIdAsync(Guid districtId, CancellationToken cancellationToken = default);

    Task<Guid?> FindDistrictIdByCodeAsync(string code, CancellationToken cancellationToken = default);

    // Every listing owned by this user, in every status — SetHomePointAsync copies the home point
    // onto all of them regardless of moderation state.
    Task<IReadOnlyCollection<Listing>> GetListingsByOwnerIdAsync(Guid ownerId, CancellationToken cancellationToken = default);

    // Any listing at all, any status — backs the DELETE me/home-point "in use" guard.
    Task<bool> OwnerHasAnyListingAsync(Guid ownerId, CancellationToken cancellationToken = default);

    // Bookings in flight (Pending/Approved/Active) against this owner's listings, with Renter and
    // Listing loaded — backs the best-effort Pickup notification fan-out after a home point moves.
    Task<IReadOnlyCollection<Booking>> GetInFlightBookingsByOwnerAsync(Guid ownerId, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
