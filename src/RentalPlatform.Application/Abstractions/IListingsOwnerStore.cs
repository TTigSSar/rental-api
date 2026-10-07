using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Abstractions;

public interface IListingsOwnerStore
{
    Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<bool> CategoryExistsAsync(Guid categoryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a new listing whose location is its owner's home point, re-reading that owner row
    /// inside the same transaction as the insert and letting the caller re-assert the location from
    /// it. Returns false when the owner has no home point in that fresh read, in which case nothing
    /// is inserted.
    /// </summary>
    /// <param name="applyOwnerHomePoint">
    /// Called exactly once, with the owner row as it is INSIDE the transaction, immediately before
    /// the insert. It copies the home point onto the listing and returns false to abort (no home
    /// point any more). The caller owns this rule — the store only owns the atomicity.
    /// </param>
    /// <remarks>
    /// This is the only insert path for a listing, deliberately: the plain "add, then save" it
    /// replaced let a create that overlapped a <c>PUT /api/auth/me/home-point</c> commit the
    /// owner's OLD point, because it read <c>user.Home*</c>, then ran two more queries, then saved.
    /// The home-point writer only relocates listings that are already committed, so the losing row
    /// kept advertising the owner's previous area in search, on the map and in the radius filter
    /// until <c>ListingLocationBackfillRunner</c>'s second pass — which runs once, at startup, on a
    /// process that stays up for weeks.
    /// </remarks>
    Task<bool> TryAddListingAtOwnerHomePointAsync(
        Listing listing,
        Func<User, bool> applyOwnerHomePoint,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<Listing>> GetListingsByOwnerIdAsync(Guid ownerId, CancellationToken cancellationToken = default);
    // Newest-first, non-empty City of a listing this owner already has (null when they have none) —
    // the fallback used on create when the owner's home point resolved to no Yerevan district.
    Task<string?> FindMostRecentListingCityAsync(Guid ownerId, CancellationToken cancellationToken = default);
    Task<Listing?> FindListingByIdWithImagesAsync(Guid listingId, CancellationToken cancellationToken = default);
    Task<Listing?> FindListingByIdAndOwnerAsync(Guid listingId, Guid ownerId, CancellationToken cancellationToken = default);
    Task AddListingImagesAsync(IEnumerable<ListingImage> images, CancellationToken cancellationToken = default);
    void RemoveListingImage(ListingImage image);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
