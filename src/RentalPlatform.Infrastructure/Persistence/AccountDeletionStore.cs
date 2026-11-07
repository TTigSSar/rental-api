using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Infrastructure.Persistence;

// The writes behind account deletion (ADR-056 and its amendments). One transaction per deletion, owned by
// the session; Users is the FIRST table touched (lock order, ADR-029), and everything else is bulk
// ExecuteUpdate/ExecuteDelete so no stale tracked copy can write anything back.
public sealed class AccountDeletionStore : IAccountDeletionStore
{
    private readonly AppDbContext _dbContext;

    public AccountDeletionStore(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IAccountDeletionSession?> BeginAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Conditional lock of the user row. The no-op SET still takes the exclusive row lock, which holds
            // until the commit: it serialises this against a concurrent deletion, a password change, and a
            // booking create on this user's listings (BookingsStore.TryCreateBookingAsync reads the owner).
            var locked = await _dbContext.Users
                .Where(user => user.Id == userId && user.DeletedAt == null)
                .ExecuteUpdateAsync(set => set.SetProperty(user => user.CreatedAt, user => user.CreatedAt), cancellationToken);
            if (locked != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                await transaction.DisposeAsync();
                return null;
            }

            var account = await _dbContext.Users
                .AsNoTracking()
                .Where(user => user.Id == userId)
                .Select(user => new AccountSnapshot(
                    user.Id, user.Email, user.PasswordHash, user.PreferredLanguage, user.AvatarUrl, user.Role))
                .FirstAsync(cancellationToken);

            return new Session(_dbContext, transaction, account);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    private sealed class Session : IAccountDeletionSession
    {
        private const string ChatReportLabelPrefix = "Chat with ";

        private readonly AppDbContext _dbContext;
        private readonly IDbContextTransaction _transaction;

        public Session(AppDbContext dbContext, IDbContextTransaction transaction, AccountSnapshot account)
        {
            _dbContext = dbContext;
            _transaction = transaction;
            Account = account;
        }

        public AccountSnapshot Account { get; }

        public async Task<IReadOnlyList<OpenRental>> FindOpenRentalsAsync(CancellationToken cancellationToken = default)
        {
            var userId = Account.Id;
            var bookings = await _dbContext.Bookings
                .AsNoTracking()
                .Where(booking => (booking.Status == BookingStatus.Approved || booking.Status == BookingStatus.Active)
                                  && (booking.RenterId == userId || booking.Listing.OwnerId == userId))
                .OrderBy(booking => booking.StartDate)
                .ThenBy(booking => booking.Id)
                .Select(booking => new
                {
                    booking.Id,
                    booking.Listing.Title,
                    ImageUrl = booking.Listing.Images
                        .OrderByDescending(image => image.IsPrimary)
                        .ThenBy(image => image.SortOrder)
                        .Select(image => image.Url)
                        .FirstOrDefault(),
                    booking.StartDate,
                    booking.EndDate,
                    IsRenter = booking.RenterId == userId
                })
                .ToListAsync(cancellationToken);

            return bookings
                .Select(booking => new OpenRental(
                    booking.Id,
                    booking.Title,
                    booking.ImageUrl,
                    booking.StartDate,
                    booking.EndDate,
                    booking.IsRenter ? "renter" : "owner"))
                .ToList();
        }

        public async Task<IReadOnlyList<ClosedBooking>?> ClosePendingBookingsAsync(
            DateTime now, CancellationToken cancellationToken = default)
        {
            var userId = Account.Id;
            var pending = await _dbContext.Bookings
                .AsNoTracking()
                .Include(booking => booking.Listing).ThenInclude(listing => listing.Images)
                .Include(booking => booking.Listing).ThenInclude(listing => listing.Owner)
                .Include(booking => booking.Renter)
                .Where(booking => booking.Status == BookingStatus.Pending
                                  && (booking.RenterId == userId || booking.Listing.OwnerId == userId))
                .ToListAsync(cancellationToken);

            var closed = new List<ClosedBooking>(pending.Count);
            foreach (var booking in pending)
            {
                var rejected = booking.Listing.OwnerId == userId;
                var target = rejected ? BookingStatus.Rejected : BookingStatus.Cancelled;
                var bookingId = booking.Id;

                // Compare-and-set on Status = Pending: of a concurrent approve/cancel/expiry and this close,
                // exactly one wins. A lost CAS aborts the whole deletion (the caller rolls back).
                var updated = await _dbContext.Bookings
                    .Where(candidate => candidate.Id == bookingId && candidate.Status == BookingStatus.Pending)
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(candidate => candidate.Status, target)
                        .SetProperty(candidate => candidate.UpdatedAt, now), cancellationToken);
                if (updated != 1)
                {
                    return null;
                }

                booking.Status = target;
                booking.UpdatedAt = now;
                closed.Add(new ClosedBooking(booking, rejected));
            }

            // Re-check inside the same transaction, after the closes: an approve that committed between
            // FindOpenRentalsAsync and the Pending read above, or a booking created by the user in that window,
            // is not covered by the lock on the user row. Anything still open -> treated as a lost CAS (409).
            var stillOpen = await _dbContext.Bookings
                .AsNoTracking()
                .AnyAsync(booking => (booking.Status == BookingStatus.Pending
                                      || booking.Status == BookingStatus.Approved
                                      || booking.Status == BookingStatus.Active)
                                     && (booking.RenterId == userId || booking.Listing.OwnerId == userId),
                    cancellationToken);
            if (stillOpen)
            {
                return null;
            }

            return closed;
        }

        public async Task EraseAsync(DateTime now, string deletedUserLabel, CancellationToken cancellationToken = default)
        {
            var userId = Account.Id;

            var names = await _dbContext.Users
                .AsNoTracking()
                .Where(user => user.Id == userId)
                .Select(user => new { user.FirstName, user.LastName })
                .FirstAsync(cancellationToken);
            var fullName = $"{names.FirstName} {names.LastName}".Trim();

            // Own listings: archived, and every place that points at the home or the pickup address is cleared
            // (amendment 2026-11-04 section 2). The pre-home-point snapshots go with them.
            await _dbContext.ListingLocationsBeforeHomePoint
                .Where(snapshot => _dbContext.Listings.Any(listing => listing.Id == snapshot.ListingId && listing.OwnerId == userId))
                .ExecuteDeleteAsync(cancellationToken);

            await _dbContext.Listings
                .Where(listing => listing.OwnerId == userId)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(listing => listing.Status, ListingStatus.Archived)
                    .SetProperty(listing => listing.AddressLine, (string?)null)
                    .SetProperty(listing => listing.Latitude, (decimal?)null)
                    .SetProperty(listing => listing.Longitude, (decimal?)null)
                    .SetProperty(listing => listing.PublicLatitude, (decimal?)null)
                    .SetProperty(listing => listing.PublicLongitude, (decimal?)null)
                    .SetProperty(listing => listing.UpdatedAt, now), cancellationToken);

            await _dbContext.Favorites
                .Where(favorite => favorite.UserId == userId)
                .ExecuteDeleteAsync(cancellationToken);

            // The user's own notifications, and everybody else's non-system notifications on the bookings,
            // conversations and reviews the user was a party to: their Title/Body embed the user's first name
            // (ADR-025), so scrubbing ActorName would not be enough (amendment 2026-11-06 section 1). The
            // notifications the closing of Pending bookings produces are emitted AFTER the commit, so they
            // are not touched here.
            await _dbContext.Notifications
                .Where(notification => notification.RecipientId == userId)
                .ExecuteDeleteAsync(cancellationToken);

            var partyEntityIds = await PartyEntityIdsAsync(userId, cancellationToken);
            if (partyEntityIds.Count > 0)
            {
                await _dbContext.Notifications
                    .Where(notification => notification.RecipientId != userId
                                           && !notification.ActorIsSystem
                                           && notification.EntityType != NotificationEntityType.Listing
                                           && partyEntityIds.Contains(notification.EntityId))
                    .ExecuteDeleteAsync(cancellationToken);
            }

            await _dbContext.UserTokens
                .Where(token => token.UserId == userId)
                .ExecuteDeleteAsync(cancellationToken);

            // Meeting-point coordinates in messages the user sent (ADR-053, amendment section 3): the row stays.
            await _dbContext.ChatMessages
                .Where(message => message.SenderId == userId
                                  && (message.MeetingPointLatitude != null || message.MeetingPointLongitude != null))
                .ExecuteUpdateAsync(set => set
                    .SetProperty(message => message.MeetingPointLatitude, (decimal?)null)
                    .SetProperty(message => message.MeetingPointLongitude, (decimal?)null), cancellationToken);

            // Search metadata stays, detached from the person.
            await _dbContext.AiSearchLogs
                .Where(log => log.UserId == userId)
                .ExecuteUpdateAsync(set => set.SetProperty(log => log.UserId, (Guid?)null), cancellationToken);

            // Admin-facing snapshots that name the user.
            // Moderation-log rows written when a report was resolved/dismissed/reopened carry the report's label
            // (TargetType = Report, TargetId = the report id), so they are relabeled with the same wording.
            var userReportIds = await _dbContext.Reports
                .AsNoTracking()
                .Where(report => report.TargetType == ReportTargetType.User && report.TargetId == userId)
                .Select(report => report.Id)
                .ToListAsync(cancellationToken);
            if (userReportIds.Count > 0)
            {
                await _dbContext.Reports
                    .Where(report => userReportIds.Contains(report.Id))
                    .ExecuteUpdateAsync(set => set.SetProperty(report => report.TargetLabel, deletedUserLabel), cancellationToken);
                await _dbContext.ModerationLogEntries
                    .Where(entry => entry.TargetType == ModerationTargetType.Report && userReportIds.Contains(entry.TargetId))
                    .ExecuteUpdateAsync(set => set.SetProperty(entry => entry.TargetLabel, deletedUserLabel), cancellationToken);
            }

            // A Message report's TargetId is the conversation id. Only reports on the user's own conversations,
            // filed by the other party, and labeled with this user's name are relabeled: a same-named stranger's
            // chat is untouched.
            if (fullName.Length > 0)
            {
                var chatLabel = ChatReportLabelPrefix + fullName;
                var newChatLabel = ChatReportLabelPrefix + deletedUserLabel;
                var conversationIds = await _dbContext.Conversations
                    .AsNoTracking()
                    .Where(conversation => conversation.OwnerId == userId || conversation.RenterId == userId)
                    .Select(conversation => conversation.Id)
                    .ToListAsync(cancellationToken);
                var chatReportIds = conversationIds.Count == 0
                    ? new List<Guid>()
                    : await _dbContext.Reports
                        .AsNoTracking()
                        .Where(report => report.TargetType == ReportTargetType.Message
                                         && report.ReporterUserId != userId
                                         && report.TargetLabel == chatLabel
                                         && conversationIds.Contains(report.TargetId))
                        .Select(report => report.Id)
                        .ToListAsync(cancellationToken);
                if (chatReportIds.Count > 0)
                {
                    await _dbContext.Reports
                        .Where(report => chatReportIds.Contains(report.Id))
                        .ExecuteUpdateAsync(set => set.SetProperty(report => report.TargetLabel, newChatLabel), cancellationToken);
                    await _dbContext.ModerationLogEntries
                        .Where(entry => entry.TargetType == ModerationTargetType.Report && chatReportIds.Contains(entry.TargetId))
                        .ExecuteUpdateAsync(set => set.SetProperty(entry => entry.TargetLabel, newChatLabel), cancellationToken);
                }
            }

            await _dbContext.ModerationLogEntries
                .Where(entry => entry.TargetType == ModerationTargetType.User && entry.TargetId == userId)
                .ExecuteUpdateAsync(set => set.SetProperty(entry => entry.TargetLabel, deletedUserLabel), cancellationToken);

            // The row itself: anonymized in one UPDATE together with the session rotation (ADR-055).
            var anonymizedEmail = $"deleted-{userId}@deleted.invalid";
            var updated = await _dbContext.Users
                .Where(user => user.Id == userId && user.DeletedAt == null)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(user => user.FirstName, string.Empty)
                    .SetProperty(user => user.LastName, string.Empty)
                    .SetProperty(user => user.PhoneNumber, (string?)null)
                    .SetProperty(user => user.AvatarUrl, (string?)null)
                    .SetProperty(user => user.Location, (string?)null)
                    .SetProperty(user => user.PreferredLanguage, (string?)null)
                    .SetProperty(user => user.PasswordHash, string.Empty)
                    .SetProperty(user => user.ExternalAuthProvider, (string?)null)
                    .SetProperty(user => user.ExternalProviderId, (string?)null)
                    .SetProperty(user => user.HomeLatitude, (decimal?)null)
                    .SetProperty(user => user.HomeLongitude, (decimal?)null)
                    .SetProperty(user => user.HomePublicLatitude, (decimal?)null)
                    .SetProperty(user => user.HomePublicLongitude, (decimal?)null)
                    .SetProperty(user => user.HomeDistrictId, (Guid?)null)
                    .SetProperty(user => user.HomePointUpdatedAt, (DateTime?)null)
                    .SetProperty(user => user.IsPhoneConfirmed, false)
                    .SetProperty(user => user.IsIdConfirmed, false)
                    .SetProperty(user => user.Email, anonymizedEmail)
                    .SetProperty(user => user.DeletedAt, (DateTime?)now)
                    .SetProperty(user => user.TokenVersion, user => user.TokenVersion + 1), cancellationToken);
            if (updated != 1)
            {
                // Cannot happen while the row lock is held; fail loudly rather than commit a half erasure.
                throw new InvalidOperationException("The account row could not be anonymized.");
            }
        }

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            await _transaction.CommitAsync(cancellationToken);

            // Bulk UPDATE/DELETE bypassed the tracker: nothing tracked about this user can be trusted any more.
            _dbContext.ChangeTracker.Clear();
        }

        public ValueTask DisposeAsync() => _transaction.DisposeAsync();

        // Ids that a non-system notification of another recipient can point at: the user's bookings and
        // conversations, and the reviews they wrote or received (and the bookings those belong to).
        private async Task<List<Guid>> PartyEntityIdsAsync(Guid userId, CancellationToken cancellationToken)
        {
            var ids = new HashSet<Guid>();

            ids.UnionWith(await _dbContext.Bookings.AsNoTracking()
                .Where(booking => booking.RenterId == userId || booking.Listing.OwnerId == userId)
                .Select(booking => booking.Id)
                .ToListAsync(cancellationToken));

            ids.UnionWith(await _dbContext.Conversations.AsNoTracking()
                .Where(conversation => conversation.OwnerId == userId || conversation.RenterId == userId)
                .Select(conversation => conversation.Id)
                .ToListAsync(cancellationToken));

            ids.UnionWith(await _dbContext.ToyReviews.AsNoTracking()
                .Where(review => review.ReviewerId == userId || review.Listing.OwnerId == userId)
                .Select(review => review.Id)
                .ToListAsync(cancellationToken));

            ids.UnionWith(await _dbContext.OwnerReviews.AsNoTracking()
                .Where(review => review.ReviewerId == userId || review.OwnerId == userId)
                .Select(review => review.Id)
                .ToListAsync(cancellationToken));

            ids.UnionWith(await _dbContext.RenterReviews.AsNoTracking()
                .Where(review => review.ReviewerId == userId || review.RenterId == userId)
                .Select(review => review.Id)
                .ToListAsync(cancellationToken));

            return ids.ToList();
        }
    }
}
