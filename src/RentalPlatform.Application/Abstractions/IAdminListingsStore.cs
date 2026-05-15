using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Abstractions;

public interface IAdminListingsStore
{
    Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Returns all PendingApproval listings with Owner, Category, and Images loaded.</summary>
    Task<IReadOnlyCollection<Listing>> GetPendingListingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the listing by id with Owner and Category loaded (tracked for update).</summary>
    Task<Listing?> FindListingByIdAsync(Guid listingId, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
