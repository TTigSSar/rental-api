using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Abstractions;

public interface IAdminListingsStore
{
    Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<Listing>> GetPendingListingsAsync(CancellationToken cancellationToken = default);
    Task<Listing?> FindListingByIdAsync(Guid listingId, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
