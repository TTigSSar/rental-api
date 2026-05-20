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

    public async Task AddListingAsync(Listing listing, CancellationToken cancellationToken = default) =>
        await _dbContext.Listings.AddAsync(listing, cancellationToken);

    public async Task<IReadOnlyCollection<Listing>> GetListingsByOwnerIdAsync(
        Guid ownerId,
        CancellationToken cancellationToken = default) =>
        await _dbContext.Listings
            .AsNoTracking()
            .Where(listing => listing.OwnerId == ownerId)
            .Include(listing => listing.Category)
            .Include(listing => listing.Images)
            .ToListAsync(cancellationToken);

    public Task<Listing?> FindListingByIdWithImagesAsync(Guid listingId, CancellationToken cancellationToken = default) =>
        _dbContext.Listings
            .Include(listing => listing.Images)
            .FirstOrDefaultAsync(listing => listing.Id == listingId, cancellationToken);

    public async Task AddListingImagesAsync(IEnumerable<ListingImage> images, CancellationToken cancellationToken = default) =>
        await _dbContext.ListingImages.AddRangeAsync(images, cancellationToken);

    public void RemoveListingImage(ListingImage image) =>
        _dbContext.ListingImages.Remove(image);

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        await _dbContext.SaveChangesAsync(cancellationToken);
}
