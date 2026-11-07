using System.Data;
using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain;
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

    public async Task<IReadOnlyList<Booking>> ExpirePendingAsync(DateTime utcNow, CancellationToken cancellationToken = default)
    {
        var expiring = await _dbContext.Bookings
            .AsNoTracking()
            .Where(booking => booking.Status == BookingStatus.Pending && booking.ExpiresAt <= utcNow)
            .ToListAsync(cancellationToken);
        if (expiring.Count == 0)
        {
            return expiring;
        }

        var ids = expiring.Select(booking => booking.Id).ToList();
        // Status re-checked so a row that left Pending between the read and the update is skipped.
        await _dbContext.Bookings
            .Where(booking => ids.Contains(booking.Id) && booking.Status == BookingStatus.Pending)
            .ExecuteUpdateAsync(update => update
                .SetProperty(booking => booking.Status, BookingStatus.Expired)
                .SetProperty(booking => booking.UpdatedAt, utcNow), cancellationToken);

        // Return only the rows this sweep really moved: a row cancelled/approved in the window
        // between the read and the update was skipped and must not get an "expired" line.
        // (Status == Expired with UpdatedAt == utcNow is the marker the update just wrote.)
        return await _dbContext.Bookings
            .AsNoTracking()
            .Where(booking => ids.Contains(booking.Id)
                && booking.Status == BookingStatus.Expired
                && booking.UpdatedAt == utcNow)
            .ToListAsync(cancellationToken);
    }

    public Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _dbContext.Users.FirstOrDefaultAsync(user => user.Id == userId, cancellationToken);

    public Task<Listing?> FindListingByIdAsync(Guid listingId, CancellationToken cancellationToken = default) =>
        _dbContext.Listings.Include(listing => listing.Owner).FirstOrDefaultAsync(listing => listing.Id == listingId, cancellationToken);

    public Task<bool> IsListingOwnerDeletedAsync(Guid listingId, CancellationToken cancellationToken = default) =>
        _dbContext.Listings
            .AsNoTracking()
            .AnyAsync(listing => listing.Id == listingId && listing.Owner.DeletedAt != null, cancellationToken);

    public async Task<bool> TryCreateBookingAsync(
        Booking booking,
        Guid? districtId,
        Guid categoryId,
        CancellationToken cancellationToken = default)
    {
        // ADR-056 amendment 6, and the lock order of ADR-029 (Users before everything else). An account deletion
        // holds the owner's Users row lock from its first statement until it commits and then writes Listings and
        // Bookings. So the owner is read FIRST in this transaction, before any lock on Listings or Bookings is
        // taken: the read waits for a deletion in progress and then sees DeletedAt, and it can never hold a
        // Listings lock while waiting for the Users row (that order deadlocked against the deletion, SQL error 1205).
        // The owner id comes from an ordinary read taken outside the transaction, which leaves no lock behind.
        var ownerId = await _dbContext.Listings
            .AsNoTracking()
            .Where(listing => listing.Id == booking.ListingId)
            .Select(listing => (Guid?)listing.OwnerId)
            .FirstOrDefaultAsync(cancellationToken);

        // SERIALIZABLE prevents two concurrent transactions from both passing the overlap
        // check and inserting overlapping bookings: SQL Server holds a key-range lock during
        // the AnyAsync read, so the second transaction blocks until the first commits/rolls back.
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        if (ownerId is { } owner)
        {
            var ownerDeleted = await _dbContext.Users
                .AnyAsync(user => user.Id == owner && user.DeletedAt != null, cancellationToken);
            if (ownerDeleted)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        // The same for the renter (a self-service create racing the renter's own deletion): the read waits for the
        // deletion's row lock and then refuses; if the read wins, it holds a shared lock on the renter row until
        // this commit, so the deletion's first statement waits and then finds the new Pending booking and closes it.
        var renterId = booking.RenterId;
        var renterDeleted = await _dbContext.Users
            .AnyAsync(user => user.Id == renterId && user.DeletedAt != null, cancellationToken);
        if (renterDeleted)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        // A Pending row only blocks while it is still live: a pending request past its ExpiresAt is
        // effectively dead even if the background sweep hasn't flipped it to Expired yet. This keeps
        // create-correctness independent of sweep timing.
        var now = DateTime.UtcNow;
        var newEffectiveEnd = RentalPeriod.EffectiveEnd(booking.StartDate, booking.EndDate);
        var hasBlockingOverlap = await _dbContext.Bookings.AnyAsync(other =>
            other.ListingId == booking.ListingId &&
            ((other.Status == BookingStatus.Pending && other.ExpiresAt > now) ||
             other.Status == BookingStatus.Approved ||
             other.Status == BookingStatus.Active) &&
            // ADR-043: occupied nights are [start, effEnd); back-to-back is allowed. Written inline
            // (not via RentalPeriod) so EF translates it to SQL; keep in step with RentalPeriod.
            other.StartDate < newEffectiveEnd &&
            booking.StartDate < (other.EndDate > other.StartDate ? other.EndDate : other.StartDate.AddDays(1)),
            cancellationToken);

        if (hasBlockingOverlap)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await _dbContext.Bookings.AddAsync(booking, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // P3-3: count the persisted request in the same transaction, last, so a rollback undoes both.
        await IncrementDemandCountAsync(
            YerevanCalendar.DateOf(booking.CreatedAt), districtId, categoryId, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    // EF only (no raw SQL, so the SQLite test host works). Under SERIALIZABLE the UPDATE takes key-range
    // locks on the sought key even when no row matches, so a concurrent first-row creator blocks until the
    // winner commits and then updates the inserted row. The null-district predicate is a literal IS NULL
    // (not "= @p") so it seeks the clustered (Day, DistrictId, CategoryId) key.
    private async Task IncrementDemandCountAsync(
        DateOnly day, Guid? districtId, Guid categoryId, CancellationToken cancellationToken)
    {
        var cells = _dbContext.DemandDailyCounts.Where(count => count.Day == day && count.CategoryId == categoryId);
        cells = districtId is { } district
            ? cells.Where(count => count.DistrictId == district)
            : cells.Where(count => count.DistrictId == null);

        var updated = await cells.ExecuteUpdateAsync(
            update => update.SetProperty(count => count.BookingRequests, count => count.BookingRequests + 1),
            cancellationToken);
        if (updated > 0)
        {
            return;
        }

        _dbContext.DemandDailyCounts.Add(new DemandDailyCount
        {
            Id = Guid.NewGuid(),
            Day = day,
            DistrictId = districtId,
            CategoryId = categoryId,
            BookingRequests = 1
        });
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<ApproveBookingOutcome> TryApproveBookingAsync(Booking booking, CancellationToken cancellationToken = default)
    {
        // SERIALIZABLE so two owners (or two tabs) approving overlapping pending requests on the
        // same listing cannot both pass the overlap check: the key-range lock taken by the read
        // forces the second transaction to serialize behind the first instead of double-booking.
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        var newEffectiveEnd = RentalPeriod.EffectiveEnd(booking.StartDate, booking.EndDate);
        var hasApprovedOverlap = await _dbContext.Bookings.AnyAsync(other =>
            other.ListingId == booking.ListingId &&
            other.Id != booking.Id &&
            (other.Status == BookingStatus.Approved || other.Status == BookingStatus.Active) &&
            other.StartDate < newEffectiveEnd &&
            booking.StartDate < (other.EndDate > other.StartDate ? other.EndDate : other.StartDate.AddDays(1)),
            cancellationToken);

        if (hasApprovedOverlap)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ApproveBookingOutcome.Overlap;
        }

        // Compare-and-set on Status = Pending (ADR-056): a concurrent account deletion that cancelled/rejected
        // this booking, a cancel, or the expiry sweep wins, and the approve then writes nothing. A tracked
        // SaveChanges would be an unconditional UPDATE ... WHERE Id and could resurrect a closed booking.
        var bookingId = booking.Id;
        var approvedAt = booking.ApprovedAt;
        var updatedAt = booking.UpdatedAt;
        var updated = await _dbContext.Bookings
            .Where(candidate => candidate.Id == bookingId && candidate.Status == BookingStatus.Pending)
            .ExecuteUpdateAsync(set => set
                .SetProperty(candidate => candidate.Status, BookingStatus.Approved)
                .SetProperty(candidate => candidate.ApprovedAt, approvedAt)
                .SetProperty(candidate => candidate.UpdatedAt, updatedAt), cancellationToken);

        if (updated != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            _dbContext.Entry(booking).State = EntityState.Detached;
            return ApproveBookingOutcome.NotPending;
        }

        await transaction.CommitAsync(cancellationToken);

        // The row was written by a bulk UPDATE; the tracked copy already holds those values, so mark it clean
        // and no later SaveChanges can write it again without the predicate.
        // (Setting the state alone would reset the current values to the originals.)
        var entry = _dbContext.Entry(booking);
        entry.OriginalValues.SetValues(entry.CurrentValues);
        entry.State = EntityState.Unchanged;
        return ApproveBookingOutcome.Approved;
    }

    public async Task<IReadOnlyCollection<Booking>> GetRenterBookingsAsync(
        Guid renterId,
        CancellationToken cancellationToken = default) =>
        await _dbContext.Bookings
            .AsNoTracking()
            .Where(booking => booking.RenterId == renterId)
            .Include(booking => booking.Listing)
                .ThenInclude(listing => listing.Images)
            .Include(booking => booking.Listing)
                .ThenInclude(listing => listing.Owner)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyCollection<Booking>> GetOwnerBookingRequestsAsync(
        Guid ownerId,
        CancellationToken cancellationToken = default) =>
        await _dbContext.Bookings
            .AsNoTracking()
            .Where(booking => booking.Listing.OwnerId == ownerId)
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

    public Task<Booking?> FindBookingDetailByIdAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        _dbContext.Bookings
            .Include(booking => booking.Listing)
                .ThenInclude(listing => listing.Images)
            .Include(booking => booking.Listing)
                .ThenInclude(listing => listing.Owner)
            .Include(booking => booking.Listing)
                .ThenInclude(listing => listing.Category)
            .Include(booking => booking.Renter)
            .FirstOrDefaultAsync(booking => booking.Id == bookingId, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _dbContext.SaveChangesAsync(cancellationToken);
}
