using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Persistence;

public sealed class FavoritesStore : IFavoritesStore
{
    private readonly AppDbContext _dbContext;

    public FavoritesStore(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _dbContext.Users.FirstOrDefaultAsync(user => user.Id == userId, cancellationToken);

    public Task<Listing?> FindListingByIdAsync(Guid listingId, CancellationToken cancellationToken = default) =>
        _dbContext.Listings.FirstOrDefaultAsync(listing => listing.Id == listingId, cancellationToken);

    public Task<Favorite?> FindByUserAndListingAsync(Guid userId, Guid listingId, CancellationToken cancellationToken = default) =>
        _dbContext.Favorites.FirstOrDefaultAsync(
            favorite => favorite.UserId == userId && favorite.ListingId == listingId,
            cancellationToken);

    public async Task<IReadOnlyCollection<Favorite>> GetByUserIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await _dbContext.Favorites
            .AsNoTracking()
            .Where(favorite => favorite.UserId == userId)
            .Include(favorite => favorite.Listing)
                .ThenInclude(listing => listing.Category)
            .Include(favorite => favorite.Listing)
                .ThenInclude(listing => listing.Images)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(Favorite favorite, CancellationToken cancellationToken = default) =>
        await _dbContext.Favorites.AddAsync(favorite, cancellationToken);

    public void Remove(Favorite favorite) => _dbContext.Favorites.Remove(favorite);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _dbContext.SaveChangesAsync(cancellationToken);
}
