using System.Data;
using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Persistence;

public sealed class ListingsOwnerStore : IListingsOwnerStore
{
    private readonly AppDbContext _dbContext;

    public ListingsOwnerStore(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _dbContext.Users.FirstOrDefaultAsync(user => user.Id == userId, cancellationToken);

    public Task<bool> CategoryExistsAsync(Guid categoryId, CancellationToken cancellationToken = default) =>
        _dbContext.Categories.AnyAsync(category => category.Id == categoryId, cancellationToken);

    // SERIALIZABLE, and the re-read is the point: SQL Server holds a key-range lock on the owner's
    // Users row from this SELECT until the commit, so the row the listing's location is copied from
    // cannot be updated out from under the insert. Same mechanism as BookingsStore's overlap check —
    // read and write become one unit instead of two statements with a gap between them.
    //
    // AsNoTracking is load-bearing, not an optimisation: this context is already tracking the User
    // the service read several queries ago, and a tracking query would hand that STALE instance
    // straight back (EF resolves identity from the change tracker and does not overwrite a tracked
    // entity's values). The whole purpose here is to see the row as it is now.
    public async Task<bool> TryAddListingAtOwnerHomePointAsync(
        Listing listing,
        Func<User, bool> applyOwnerHomePoint,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        var owner = await _dbContext.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(user => user.Id == listing.OwnerId, cancellationToken);

        if (owner is null || !applyOwnerHomePoint(owner))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await _dbContext.Listings.AddAsync(listing, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyCollection<Listing>> GetListingsByOwnerIdAsync(
        Guid ownerId,
        CancellationToken cancellationToken = default) =>
        await _dbContext.Listings
            .AsNoTracking()
            .Where(listing => listing.OwnerId == ownerId)
            .Include(listing => listing.Category)
            .Include(listing => listing.Images)
            .ToListAsync(cancellationToken);

    // Newest-first City of an existing listing this owner already has — the legacy fallback in
    // ListingsOwnerService.ResolveCity, for an owner whose home point sits outside every Yerevan
    // district. Only the AddUserHomePoint migration could produce such an owner (new home points
    // must be in Yerevan), and by construction they have at least one listing, so this is the one
    // place a truthful city for their next listing can come from.
    public Task<string?> FindMostRecentListingCityAsync(Guid ownerId, CancellationToken cancellationToken = default) =>
        _dbContext.Listings
            .AsNoTracking()
            .Where(listing => listing.OwnerId == ownerId && listing.City != "")
            .OrderByDescending(listing => listing.CreatedAt)
            .ThenByDescending(listing => listing.Id)
            .Select(listing => (string?)listing.City)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<Listing?> FindListingByIdWithImagesAsync(Guid listingId, CancellationToken cancellationToken = default) =>
        _dbContext.Listings
            .Include(listing => listing.Images)
            .FirstOrDefaultAsync(listing => listing.Id == listingId, cancellationToken);

    public Task<Listing?> FindListingByIdAndOwnerAsync(Guid listingId, Guid ownerId, CancellationToken cancellationToken = default) =>
        _dbContext.Listings
            .FirstOrDefaultAsync(listing => listing.Id == listingId && listing.OwnerId == ownerId, cancellationToken);

    public async Task AddListingImagesAsync(IEnumerable<ListingImage> images, CancellationToken cancellationToken = default) =>
        await _dbContext.ListingImages.AddRangeAsync(images, cancellationToken);

    public void RemoveListingImage(ListingImage image) =>
        _dbContext.ListingImages.Remove(image);

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        await _dbContext.SaveChangesAsync(cancellationToken);
}
