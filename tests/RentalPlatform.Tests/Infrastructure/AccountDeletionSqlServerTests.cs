using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// ADR-056 against a REAL SQL Server, for what SQLite cannot prove: that the bulk ExecuteUpdate/ExecuteDelete
// statements of the erasure translate to T-SQL and run over the real migrated schema, and that the lock on the
// user row taken first (ADR-029 order) really makes a concurrent booking create wait and then refuse
// (amendment 2026-11-04 section 6). [SqlServerFact] skips with a reason when no SQL Server is reachable.
[Collection(RentalPlatform.Tests.TestSupport.SqlServerConcurrencyCollection.Name)]
public sealed class AccountDeletionSqlServerTests
{
    private static readonly Guid UserId = new("b3000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherId = new("b3000000-0000-0000-0000-000000000002");
    private static readonly Guid CategoryId = new("b3000000-0000-0000-0000-000000000003");
    private static readonly Guid UserListingId = new("b3000000-0000-0000-0000-000000000004");
    private static readonly Guid OtherListingId = new("b3000000-0000-0000-0000-000000000005");

    private static async Task SeedAsync(SqlServerTestDatabase db)
    {
        await db.SeedAsync(
            TestData.OwnerWithHome(UserId, "sql.gone@test.local", TestData.KentronPoint),
            TestData.OwnerWithHome(OtherId, "sql.other@test.local", TestData.ArabkirPoint),
            TestData.Category(CategoryId));
        await db.SeedAsync(
            TestData.Listing(UserListingId, UserId, CategoryId),
            TestData.Listing(OtherListingId, OtherId, CategoryId));
    }

    [SqlServerFact]
    public async Task The_Whole_Erasure_Runs_On_SQL_Server()
    {
        using var db = new SqlServerTestDatabase();
        await SeedAsync(db);
        var pending = Guid.NewGuid();
        var incoming = Guid.NewGuid();
        var completed = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var otherNotification = Guid.NewGuid();
        var start = TestData.Today.AddDays(5);
        await db.SeedAsync(
            TestData.Booking(pending, OtherListingId, UserId, start, start.AddDays(1), BookingStatus.Pending),
            TestData.Booking(incoming, UserListingId, OtherId, start.AddDays(10), start.AddDays(11), BookingStatus.Pending),
            TestData.Booking(completed, OtherListingId, UserId, TestData.Today.AddDays(-9), TestData.Today.AddDays(-8), BookingStatus.Completed));
        await db.SeedAsync(TestData.Conversation(conversationId, completed, OtherId, UserId));
        var point = TestData.ChatMessage(messageId, conversationId, UserId, null, MessageType.Location);
        point.MeetingPointLatitude = 40.1856m;
        point.MeetingPointLongitude = 44.5126m;
        await db.SeedAsync(
            point,
            new Favorite { Id = Guid.NewGuid(), UserId = UserId, ListingId = OtherListingId, CreatedAt = DateTime.UtcNow },
            new ListingLocationBeforeHomePoint { ListingId = UserListingId, City = "Yerevan", Country = "Armenia", CapturedAt = DateTime.UtcNow },
            new AiSearchLog { Id = Guid.NewGuid(), UserId = UserId, CreatedAt = DateTime.UtcNow, Outcome = "ok" },
            new Notification
            {
                Id = otherNotification, RecipientId = OtherId, Kind = NotificationKind.Request, Category = NotificationCategory.Booking,
                Title = "t", Body = "b", ActorName = "Test User", EntityType = NotificationEntityType.Booking, EntityId = completed,
                DeepLink = "/x", CreatedAt = DateTime.UtcNow
            },
            TestData.Report(Guid.NewGuid(), ReportTargetType.User, UserId, OtherId, targetLabel: "Test User"));

        await using var context = db.CreateContext();
        var service = new AccountDeletionService(
            new FakeCurrentUserContext(UserId),
            new AccountDeletionStore(context),
            new FakePasswordHasher(),
            new FakeNotificationEmitter(),
            new FakeChatSystemMessageEmitter(),
            new FakeSessionRevoker(),
            new FakeFileStorageService(),
            new FakeEmailService(),
            new FakeEmailSendBudget(),
            TimeProvider.System);
        await using (var seed = db.CreateContext())
        {
            await seed.Users.Where(user => user.Id == UserId).ExecuteUpdateAsync(set => set.SetProperty(user => user.PasswordHash, "hashed:pw"));
        }

        var result = await service.DeleteCurrentUserAsync(new RentalPlatform.Application.DTOs.DeleteAccountRequest { Password = "pw" });

        Assert.True(result.IsSuccess, result.Error?.Message);
        await using var verify = db.CreateContext();
        var stored = await verify.Users.AsNoTracking().SingleAsync(user => user.Id == UserId);
        Assert.NotNull(stored.DeletedAt);
        Assert.Equal($"deleted-{UserId}@deleted.invalid", stored.Email);
        Assert.Equal(1, stored.TokenVersion);
        Assert.Equal(BookingStatus.Cancelled, (await verify.Bookings.AsNoTracking().SingleAsync(booking => booking.Id == pending)).Status);
        Assert.Equal(BookingStatus.Rejected, (await verify.Bookings.AsNoTracking().SingleAsync(booking => booking.Id == incoming)).Status);
        Assert.Equal(ListingStatus.Archived, (await verify.Listings.AsNoTracking().SingleAsync(listing => listing.Id == UserListingId)).Status);
        Assert.Empty(await verify.Favorites.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.ListingLocationsBeforeHomePoint.AsNoTracking().ToListAsync());
        Assert.Null((await verify.AiSearchLogs.AsNoTracking().SingleAsync()).UserId);
        Assert.Null((await verify.ChatMessages.AsNoTracking().SingleAsync(message => message.Id == messageId)).MeetingPointLatitude);
        Assert.DoesNotContain(await verify.Notifications.AsNoTracking().ToListAsync(), notification => notification.Id == otherNotification);
        Assert.Equal("Deleted user", (await verify.Reports.AsNoTracking().SingleAsync()).TargetLabel);
    }

    [SqlServerFact]
    public async Task A_Booking_Create_Waits_For_A_Deletion_In_Progress_And_Is_Then_Refused()
    {
        using var db = new SqlServerTestDatabase();
        await SeedAsync(db);

        // The deletion has taken its first statement (the lock on the owner's row) and is still open.
        await using var deletionContext = db.CreateContext();
        var session = await new AccountDeletionStore(deletionContext).BeginAsync(UserId);
        Assert.NotNull(session);

        var start = TestData.Today.AddDays(6);
        var booking = TestData.Booking(Guid.NewGuid(), UserListingId, OtherId, start, start.AddDays(1), BookingStatus.Pending);
        await using var createContext = db.CreateContext();
        var create = Task.Run(() => new BookingsStore(createContext).TryCreateBookingAsync(booking, null, Guid.NewGuid()));

        await db.WaitForBlockedRequestAsync(TimeSpan.FromSeconds(30), create);
        Assert.False(create.IsCompleted, "the create must wait on the owner's row lock while the deletion is open");

        await session!.ClosePendingBookingsAsync(DateTime.UtcNow);
        await session.EraseAsync(DateTime.UtcNow, "Deleted user");
        await session.CommitAsync();
        await session.DisposeAsync();

        Assert.False(await create.WaitAsync(TimeSpan.FromSeconds(30)));
        await using var verify = db.CreateContext();
        Assert.Empty(await verify.Bookings.AsNoTracking().ToListAsync());
    }

    [SqlServerFact]
    public async Task A_Booking_Create_By_The_Deleted_Renter_Waits_And_Is_Then_Refused()
    {
        using var db = new SqlServerTestDatabase();
        await SeedAsync(db);

        await using var deletionContext = db.CreateContext();
        var session = await new AccountDeletionStore(deletionContext).BeginAsync(UserId);
        Assert.NotNull(session);

        // The renter being deleted creates a request on somebody else's listing.
        var start = TestData.Today.AddDays(6);
        var booking = TestData.Booking(Guid.NewGuid(), OtherListingId, UserId, start, start.AddDays(1), BookingStatus.Pending);
        await using var createContext = db.CreateContext();
        var create = Task.Run(() => new BookingsStore(createContext).TryCreateBookingAsync(booking, null, CategoryId));

        await db.WaitForBlockedRequestAsync(TimeSpan.FromSeconds(30), create);
        Assert.False(create.IsCompleted, "the create must wait on the renter's row lock while the deletion is open");

        Assert.NotNull(await session!.ClosePendingBookingsAsync(DateTime.UtcNow));
        await session.EraseAsync(DateTime.UtcNow, "Deleted user");
        await session.CommitAsync();
        await session.DisposeAsync();

        Assert.False(await create.WaitAsync(TimeSpan.FromSeconds(30)));
        await using var verify = db.CreateContext();
        Assert.Empty(await verify.Bookings.AsNoTracking().ToListAsync());
    }

    // The opposite ordering: the create's renter read wins and holds its shared lock on the renter row until the
    // create commits (SERIALIZABLE), so the deletion's first statement waits, then finds the new Pending booking
    // and closes it (Cancelled). Invariant: no Pending/Approved/Active booking survives for the deleted user.
    [SqlServerFact]
    public async Task A_Booking_Create_That_Read_The_Renter_First_Makes_The_Deletion_Wait_And_Is_Then_Closed()
    {
        using var db = new SqlServerTestDatabase();
        await SeedAsync(db);

        var start = TestData.Today.AddDays(6);
        var bookingId = Guid.NewGuid();
        var booking = TestData.Booking(bookingId, OtherListingId, UserId, start, start.AddDays(1), BookingStatus.Pending);

        // Pauses the create just before its overlap read, i.e. after both Users reads (their shared locks are held).
        var pause = new PauseBeforeOverlapReadInterceptor();
        await using var createContext = db.CreateContext(pause);
        var create = Task.Run(() => new BookingsStore(createContext).TryCreateBookingAsync(booking, null, CategoryId));
        await Task.WhenAny(pause.Reached.Task, create).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(pause.Reached.Task.IsCompleted, "the create must reach the overlap read (and hold the renter lock)");

        await using var deletionContext = db.CreateContext();
        var begin = Task.Run(() => new AccountDeletionStore(deletionContext).BeginAsync(UserId));
        await db.WaitForBlockedRequestAsync(TimeSpan.FromSeconds(30), begin, create);
        Assert.False(begin.IsCompleted, "the deletion must wait on the renter's row while the create holds its lock");

        pause.Release.SetResult();
        Assert.True(await create.WaitAsync(TimeSpan.FromSeconds(30)));

        var session = await begin.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(session);
        var closed = await session!.ClosePendingBookingsAsync(DateTime.UtcNow);
        Assert.NotNull(closed);
        Assert.Equal(bookingId, Assert.Single(closed!).Booking.Id);
        await session.EraseAsync(DateTime.UtcNow, "Deleted user");
        await session.CommitAsync();
        await session.DisposeAsync();

        await using var verify = db.CreateContext();
        Assert.NotNull((await verify.Users.AsNoTracking().SingleAsync(u => u.Id == UserId)).DeletedAt);
        Assert.DoesNotContain(
            await verify.Bookings.AsNoTracking().Where(b => b.RenterId == UserId || b.Listing.OwnerId == UserId).ToListAsync(),
            b => b.Status is BookingStatus.Pending or BookingStatus.Approved or BookingStatus.Active);
        Assert.Equal(BookingStatus.Cancelled, (await verify.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId)).Status);
    }

    private sealed class PauseBeforeOverlapReadInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private int _fired;

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[Bookings]") && command.CommandText.Contains("[ExpiresAt]")
                && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                Reached.SetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
            }

            return result;
        }
    }

    // Approve read the booking as Pending, then the deletion cancelled it and committed: the approve must lose
    // the compare-and-set and leave Cancelled in place (no "deleted renter + Approved booking").
    [SqlServerFact]
    public async Task An_Approve_That_Read_Pending_Before_The_Deletion_Committed_Loses_The_Compare_And_Set()
    {
        using var db = new SqlServerTestDatabase();
        await SeedAsync(db);
        var bookingId = Guid.NewGuid();
        var start = TestData.Today.AddDays(5);
        await db.SeedAsync(TestData.Booking(bookingId, OtherListingId, UserId, start, start.AddDays(1), BookingStatus.Pending));

        await using var approveContext = db.CreateContext();
        var tracked = await new BookingsStore(approveContext).FindBookingWithRelationsByIdAsync(bookingId);
        Assert.NotNull(tracked);
        tracked!.Status = BookingStatus.Approved;
        tracked.ApprovedAt = DateTime.UtcNow;
        tracked.UpdatedAt = DateTime.UtcNow;

        await using (var deletionContext = db.CreateContext())
        {
            var session = await new AccountDeletionStore(deletionContext).BeginAsync(UserId);
            Assert.NotNull(session);
            Assert.Empty(await session!.FindOpenRentalsAsync());
            Assert.NotNull(await session.ClosePendingBookingsAsync(DateTime.UtcNow));
            await session.EraseAsync(DateTime.UtcNow, "Deleted user");
            await session.CommitAsync();
            await session.DisposeAsync();
        }

        var outcome = await new BookingsStore(approveContext).TryApproveBookingAsync(tracked);

        Assert.Equal(RentalPlatform.Application.Abstractions.ApproveBookingOutcome.NotPending, outcome);
        await using var verify = db.CreateContext();
        Assert.Equal(BookingStatus.Cancelled, (await verify.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId)).Status);
        Assert.NotNull((await verify.Users.AsNoTracking().SingleAsync(u => u.Id == UserId)).DeletedAt);
    }

    // The approve commits between the deletion's open-rentals check and its Pending read: the post-close re-check
    // catches the now-Approved booking, the close reports a lost race (409 state_changed) and nothing is deleted.
    [SqlServerFact]
    public async Task An_Approve_Committed_Between_The_Open_Rentals_Check_And_The_Close_Aborts_The_Deletion()
    {
        using var db = new SqlServerTestDatabase();
        await SeedAsync(db);
        var bookingId = Guid.NewGuid();
        var start = TestData.Today.AddDays(5);
        await db.SeedAsync(TestData.Booking(bookingId, OtherListingId, UserId, start, start.AddDays(1), BookingStatus.Pending));

        // The owner's read (it includes the renter row) happens first: once the deletion holds the renter's row
        // lock the same read would simply wait for it.
        await using var approveContext = db.CreateContext();
        var store = new BookingsStore(approveContext);
        var tracked = await store.FindBookingWithRelationsByIdAsync(bookingId);
        tracked!.Status = BookingStatus.Approved;
        tracked.ApprovedAt = DateTime.UtcNow;
        tracked.UpdatedAt = DateTime.UtcNow;

        await using var deletionContext = db.CreateContext();
        var session = await new AccountDeletionStore(deletionContext).BeginAsync(UserId);
        Assert.NotNull(session);
        Assert.Empty(await session!.FindOpenRentalsAsync());

        Assert.Equal(RentalPlatform.Application.Abstractions.ApproveBookingOutcome.Approved, await store.TryApproveBookingAsync(tracked));

        Assert.Null(await session.ClosePendingBookingsAsync(DateTime.UtcNow));
        await session.DisposeAsync(); // rolls back

        await using var verify = db.CreateContext();
        Assert.Equal(BookingStatus.Approved, (await verify.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId)).Status);
        Assert.Null((await verify.Users.AsNoTracking().SingleAsync(u => u.Id == UserId)).DeletedAt);
    }
}
