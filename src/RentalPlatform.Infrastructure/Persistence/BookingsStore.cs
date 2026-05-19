using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Infrastructure.Persistence;

public sealed class BookingsStore : IBookingsStore
{
    private readonly AppDbContext _dbContext;

    public BookingsStore(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task ExpirePendingAsync(DateTime utcNow, CancellationToken cancellationToken = default)
    {
        await _dbContext.Bookings
            .Where(booking => booking.Status == BookingStatus.Pending && booking.ExpiresAt <= utcNow)
            .ExecuteUpdateAsync(update => update
                .SetProperty(booking => booking.Status, BookingStatus.Expired)
                .SetProperty(booking => booking.UpdatedAt, utcNow), cancellationToken);
    }

    public Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _dbContext.Users.FirstOrDefaultAsync(user => user.Id == userId, cancellationToken);

    public Task<Listing?> FindListingByIdAsync(Guid listingId, CancellationToken cancellationToken = default) =>
        _dbContext.Listings.FirstOrDefaultAsync(listing => listing.Id == listingId, cancellationToken);

    public Task<bool> HasApprovedOverlapAsync(
        Guid listingId,
        DateOnly startDate,
        DateOnly endDate,
        Guid? excludedBookingId,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.Bookings.Where(booking =>
            booking.ListingId == listingId &&
            booking.Status == BookingStatus.Approved &&
            booking.StartDate <= endDate &&
            booking.EndDate >= startDate);

        if (excludedBookingId.HasValue)
        {
            query = query.Where(booking => booking.Id != excludedBookingId.Value);
        }

        return query.AnyAsync(cancellationToken);
    }

    public async Task AddBookingAsync(Booking booking, CancellationToken cancellationToken = default) =>
        await _dbContext.Bookings.AddAsync(booking, cancellationToken);

    public async Task<IReadOnlyCollection<Booking>> GetRenterBookingsAsync(
        Guid renterId,
        CancellationToken cancellationToken = default) =>
        await _dbContext.Bookings
            .AsNoTracking()
            .Where(booking => booking.RenterId == renterId)
            .Include(booking => booking.Listing)
                .ThenInclude(listing => listing.Images)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyCollection<Booking>> GetOwnerBookingRequestsAsync(
        Guid ownerId,
        CancellationToken cancellationToken = default) =>
        await _dbContext.Bookings
            .AsNoTracking()
            .Where(booking => booking.Listing.OwnerId == ownerId && booking.Status == BookingStatus.Pending)
            .Include(booking => booking.Listing)
                .ThenInclude(listing => listing.Images)
            .Include(booking => booking.Renter)
            .ToListAsync(cancellationToken);

    public Task<Booking?> FindBookingWithRelationsByIdAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        _dbContext.Bookings
            .Include(booking => booking.Listing)
                .ThenInclude(listing => listing.Images)
            .Include(booking => booking.Renter)
            .FirstOrDefaultAsync(booking => booking.Id == bookingId, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _dbContext.SaveChangesAsync(cancellationToken);
}
