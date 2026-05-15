using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Infrastructure.Persistence;

public sealed class AdminListingsStore : IAdminListingsStore
{
    private readonly AppDbContext _dbContext;

    public AdminListingsStore(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _dbContext.Users.FirstOrDefaultAsync(user => user.Id == userId, cancellationToken);

    public async Task<IReadOnlyCollection<Listing>> GetPendingListingsAsync(CancellationToken cancellationToken = default) =>
        await _dbContext.Listings
            .AsNoTracking()
            .Where(listing => listing.Status == ListingStatus.PendingApproval)
            .Include(listing => listing.Owner)
            .Include(listing => listing.Category)
            .Include(listing => listing.Images.OrderByDescending(i => i.IsPrimary).ThenBy(i => i.SortOrder))
            .AsSplitQuery()
            .OrderBy(listing => listing.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<Listing?> FindListingByIdAsync(Guid listingId, CancellationToken cancellationToken = default) =>
        _dbContext.Listings
            .Include(listing => listing.Owner)
            .Include(listing => listing.Category)
            .FirstOrDefaultAsync(listing => listing.Id == listingId, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _dbContext.SaveChangesAsync(cancellationToken);
}
