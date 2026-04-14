using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Abstractions;

public interface IFavoritesStore
{
    Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<Listing?> FindListingByIdAsync(Guid listingId, CancellationToken cancellationToken = default);
    Task<Favorite?> FindByUserAndListingAsync(Guid userId, Guid listingId, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<Favorite>> GetByUserIdAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<bool> TryAddAsync(Favorite favorite, CancellationToken cancellationToken = default);
    void Remove(Favorite favorite);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
