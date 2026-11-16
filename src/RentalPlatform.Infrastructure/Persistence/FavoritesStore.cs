using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
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
            .Include(favorite => favorite.Listing)
                .ThenInclude(listing => listing.District)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, (int Count, long Sum)>> GetReviewStatsAsync(
        IReadOnlyCollection<Guid> listingIds, CancellationToken cancellationToken = default)
    {
        var rows = await _dbContext.ToyReviews
            .AsNoTracking()
            .Where(review => listingIds.Contains(review.ListingId))
            .GroupBy(review => review.ListingId)
            .Select(group => new { ListingId = group.Key, Count = group.Count(), Sum = group.Sum(review => review.OverallRating) })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(row => row.ListingId, row => (row.Count, (long)row.Sum));
    }

    public async Task<bool> TryAddAsync(Favorite favorite, CancellationToken cancellationToken = default)
    {
        await _dbContext.Favorites.AddAsync(favorite, cancellationToken);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            _dbContext.Entry(favorite).State = EntityState.Detached;
            return false;
        }
    }

    public void Remove(Favorite favorite) => _dbContext.Favorites.Remove(favorite);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _dbContext.SaveChangesAsync(cancellationToken);

    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
    {
        if (exception.InnerException is not SqlException sqlException)
        {
            return false;
        }

        return sqlException.Number is 2601 or 2627;
    }
}
