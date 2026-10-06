using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Infrastructure.Persistence;

public sealed class HomePointStore : IHomePointStore
{
    private readonly AppDbContext _dbContext;

    public HomePointStore(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _dbContext.Users.FirstOrDefaultAsync(user => user.Id == userId, cancellationToken);

    public Task<District?> FindDistrictByIdAsync(Guid districtId, CancellationToken cancellationToken = default) =>
        _dbContext.Districts.FirstOrDefaultAsync(district => district.Id == districtId, cancellationToken);

    public Task<Guid?> FindDistrictIdByCodeAsync(string code, CancellationToken cancellationToken = default) =>
        _dbContext.Districts
            .Where(district => district.Code == code)
            .Select(district => (Guid?)district.Id)
            .FirstOrDefaultAsync(cancellationToken);

    // Tracked (no AsNoTracking) — SetHomePointAsync mutates these in place before SaveChanges.
    public async Task<IReadOnlyCollection<Listing>> GetListingsByOwnerIdAsync(Guid ownerId, CancellationToken cancellationToken = default) =>
        await _dbContext.Listings
            .Where(listing => listing.OwnerId == ownerId)
            .ToListAsync(cancellationToken);

    public Task<bool> OwnerHasAnyListingAsync(Guid ownerId, CancellationToken cancellationToken = default) =>
        _dbContext.Listings.AnyAsync(listing => listing.OwnerId == ownerId, cancellationToken);

    public async Task<IReadOnlyCollection<Booking>> GetInFlightBookingsByOwnerAsync(Guid ownerId, CancellationToken cancellationToken = default) =>
        await _dbContext.Bookings
            .Where(booking => booking.Listing.OwnerId == ownerId &&
                (booking.Status == BookingStatus.Pending ||
                 booking.Status == BookingStatus.Approved ||
                 booking.Status == BookingStatus.Active))
            .Include(booking => booking.Renter)
            .Include(booking => booking.Listing)
                // Images, not just the Listing: NotificationEmitter reads listing.Images for the
                // notification's ToyImageUrl thumbnail, lazy loading is off, and an un-included
                // collection is simply empty — so without this the pickup notification was the one
                // booking notification in the product rendering with no toy image. Every other emit
                // path loads it the same way (BookingsStore).
                .ThenInclude(listing => listing.Images)
            .ToListAsync(cancellationToken);

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        await _dbContext.SaveChangesAsync(cancellationToken);
}
