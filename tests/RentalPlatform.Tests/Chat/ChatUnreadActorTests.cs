using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Chat;

// RC-5 / ADR-001 amendment: every rental status change is an unread chat line for the OTHER
// party, never for the one who acted; a system/timer line (no actor) counts for both.
// Drives the REAL BookingsService + ChatSystemMessageEmitter + ConversationsStore on SQLite
// (M-010: no hand-set rows for the derived flag).
public sealed class ChatUnreadActorTests
{
    private static readonly Guid OwnerId = new("e0000000-0000-0000-0000-000000000001");
    private static readonly Guid RenterId = new("e0000000-0000-0000-0000-000000000002");
    private static readonly Guid CategoryId = new("e0000000-0000-0000-0000-000000000003");
    private static readonly Guid ListingId = new("e0000000-0000-0000-0000-000000000004");

    private static async Task<SqliteTestDatabase> NewDbAsync()
    {
        var db = new SqliteTestDatabase();
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@test.local"),
            TestData.User(RenterId, "renter@test.local"),
            TestData.Category(CategoryId));
        await db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId));
        return db;
    }

    private static BookingsService BookingsAs(SqliteTestDatabase db, Guid userId)
    {
        var context = db.CreateContext();
        var store = new ConversationsStore(context, NullLogger<ConversationsStore>.Instance);
        return new BookingsService(
            new FakeCurrentUserContext(userId),
            new BookingsStore(context),
            new FakeNotificationEmitter(),
            new ChatSystemMessageEmitter(store, new FakeChatRealtimeNotifier(), NullLogger<ChatSystemMessageEmitter>.Instance),
            new ReviewsStore(context),
            store);
    }

    private static async Task<int> UnreadAsync(SqliteTestDatabase db, Guid userId)
    {
        await using var context = db.CreateContext();
        var store = new ConversationsStore(context, NullLogger<ConversationsStore>.Instance);
        var items = await store.ListForUserAsync(userId);
        return items.Sum(item => item.UnreadCount);
    }

    private static async Task MarkAllReadAsync(SqliteTestDatabase db)
    {
        await using var context = db.CreateContext();
        var store = new ConversationsStore(context, NullLogger<ConversationsStore>.Instance);
        foreach (var userId in new[] { OwnerId, RenterId })
        {
            foreach (var item in await store.ListForUserAsync(userId))
            {
                await store.MarkReadAsync(item.Conversation.Id, userId);
            }
        }

        // Keep the next line strictly newer than the read cursor.
        await Task.Delay(15);
    }

    private static async Task<Guid> CreateBookingAsync(SqliteTestDatabase db)
    {
        var result = await BookingsAs(db, RenterId).CreateAsync(new CreateBookingRequest
        {
            ListingId = ListingId,
            StartDate = TestData.Today.AddDays(5),
            EndDate = TestData.Today.AddDays(8)
        });
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value!.Id;
    }

    private static async Task AssertActorZeroOtherOneAsync(SqliteTestDatabase db, Guid actor, Guid other)
    {
        Assert.Equal(0, await UnreadAsync(db, actor));
        Assert.Equal(1, await UnreadAsync(db, other));
    }

    [Fact]
    public async Task Request_Is_Unread_For_Owner_Not_Renter()
    {
        using var db = await NewDbAsync();
        await CreateBookingAsync(db);
        await AssertActorZeroOtherOneAsync(db, actor: RenterId, other: OwnerId);
    }

    [Fact]
    public async Task Approve_Handover_Complete_Each_Unread_For_Other_Party_Only()
    {
        using var db = await NewDbAsync();
        var bookingId = await CreateBookingAsync(db);
        await MarkAllReadAsync(db);

        Assert.True((await BookingsAs(db, OwnerId).ApproveAsync(bookingId)).IsSuccess);
        await AssertActorZeroOtherOneAsync(db, actor: OwnerId, other: RenterId);
        await MarkAllReadAsync(db);

        Assert.True((await BookingsAs(db, OwnerId).MarkActiveAsync(bookingId)).IsSuccess);
        await AssertActorZeroOtherOneAsync(db, actor: OwnerId, other: RenterId);
        await MarkAllReadAsync(db);

        Assert.True((await BookingsAs(db, OwnerId).CompleteAsync(bookingId)).IsSuccess);
        await AssertActorZeroOtherOneAsync(db, actor: OwnerId, other: RenterId);
    }

    [Fact]
    public async Task Reject_Is_Unread_For_Renter_Not_Owner()
    {
        using var db = await NewDbAsync();
        var bookingId = await CreateBookingAsync(db);
        await MarkAllReadAsync(db);

        Assert.True((await BookingsAs(db, OwnerId).RejectAsync(bookingId, "no")).IsSuccess);
        await AssertActorZeroOtherOneAsync(db, actor: OwnerId, other: RenterId);
    }

    [Fact]
    public async Task Cancel_Is_Unread_For_Owner_Not_Renter()
    {
        using var db = await NewDbAsync();
        var bookingId = await CreateBookingAsync(db);
        await MarkAllReadAsync(db);

        Assert.True((await BookingsAs(db, RenterId).CancelAsync(bookingId)).IsSuccess);
        await AssertActorZeroOtherOneAsync(db, actor: RenterId, other: OwnerId);
    }

    [Fact]
    public async Task System_Line_With_Null_Actor_Is_Unread_For_Both()
    {
        using var db = await NewDbAsync();
        var bookingId = await CreateBookingAsync(db);
        await MarkAllReadAsync(db);

        await using var context = db.CreateContext();
        var emitter = new ChatSystemMessageEmitter(
            new ConversationsStore(context, NullLogger<ConversationsStore>.Instance),
            new FakeChatRealtimeNotifier(),
            NullLogger<ChatSystemMessageEmitter>.Instance);
        var booking = await context.Bookings.FindAsync(bookingId);
        await emitter.BookingCompletedAsync(booking!, actorUserId: null);

        Assert.Equal(1, await UnreadAsync(db, OwnerId));
        Assert.Equal(1, await UnreadAsync(db, RenterId));
    }

    [Fact]
    public async Task Existing_Messages_Are_Unaffected()
    {
        using var db = await NewDbAsync();
        var bookingId = await CreateBookingAsync(db);
        await MarkAllReadAsync(db);

        Guid conversationId;
        await using (var context = db.CreateContext())
        {
            conversationId = (await context.Conversations.FindAsync(
                context.Conversations.Where(c => c.BookingId == bookingId).Select(c => c.Id).Single()))!.Id;
            // Pre-RC-5 rows: a text bubble from the renter and a system line with no actor.
            context.ChatMessages.Add(TestData.ChatMessage(Guid.NewGuid(), conversationId, RenterId));
            context.ChatMessages.Add(TestData.ChatMessage(Guid.NewGuid(), conversationId, null, type: MessageType.System));
            await context.SaveChangesAsync();
        }

        // Owner: renter text + legacy system line. Renter: only the legacy system line.
        Assert.Equal(2, await UnreadAsync(db, OwnerId));
        Assert.Equal(1, await UnreadAsync(db, RenterId));
    }

    [Fact]
    public void Expired_Kind_Maps_To_Expired_Token()
    {
        Assert.Equal(7, (int)ChatSystemKind.Expired);
        Assert.Equal("expired", RentalPlatform.Application.Common.ChatTokens.SystemKindToken(ChatSystemKind.Expired));
    }

    [Fact]
    public async Task Sweep_Emits_One_Expired_Line_Per_Booking_Unread_For_Both_And_Is_Idempotent()
    {
        using var db = await NewDbAsync();
        var otherListingId = Guid.NewGuid();
        await db.SeedAsync(TestData.Listing(otherListingId, OwnerId, CategoryId));
        var b1 = Guid.NewGuid();
        var b2 = Guid.NewGuid();
        var live = Guid.NewGuid();
        await db.SeedAsync(
            TestData.Booking(b1, ListingId, RenterId, TestData.Today.AddDays(5), TestData.Today.AddDays(6),
                BookingStatus.Pending, expiresAt: DateTime.UtcNow.AddHours(-1)),
            TestData.Booking(b2, otherListingId, RenterId, TestData.Today.AddDays(5), TestData.Today.AddDays(6),
                BookingStatus.Pending, expiresAt: DateTime.UtcNow.AddMinutes(-5)),
            TestData.Booking(live, otherListingId, RenterId, TestData.Today.AddDays(10), TestData.Today.AddDays(11),
                BookingStatus.Pending, expiresAt: DateTime.UtcNow.AddHours(5)));

        for (var i = 0; i < 2; i++)
        {
            await using var context = db.CreateContext();
            await BookingExpiryBackgroundService.ExpireAndAnnounceAsync(
                new BookingsStore(context),
                new ChatSystemMessageEmitter(
                    new ConversationsStore(context, NullLogger<ConversationsStore>.Instance),
                    new FakeChatRealtimeNotifier(),
                    NullLogger<ChatSystemMessageEmitter>.Instance),
                DateTime.UtcNow,
                CancellationToken.None);
        }

        await using var verify = db.CreateContext();
        var lines = verify.ChatMessages.Where(m => m.SystemKind == ChatSystemKind.Expired).ToList();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, m => Assert.Null(m.ActorUserId));
        Assert.All(lines, m => Assert.Equal("The request expired without an answer.", m.Body));
        Assert.Equal(BookingStatus.Expired, verify.Bookings.Single(b => b.Id == b1).Status);
        Assert.Equal(BookingStatus.Pending, verify.Bookings.Single(b => b.Id == live).Status);

        // Two booking conversations, one expired line each => 2 unread for each party.
        Assert.Equal(2, await UnreadAsync(db, OwnerId));
        Assert.Equal(2, await UnreadAsync(db, RenterId));
    }

    // Cancels the target booking right before the sweep's guarded UPDATE runs, i.e. after the
    // sweep has read it as Pending (the race the guarded update protects against).
    private sealed class CancelBeforeExpireUpdateInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private readonly Func<Task> _cancel;
        private bool _done;

        public CancelBeforeExpireUpdateInterceptor(Func<Task> cancel) => _cancel = cancel;

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_done && command.CommandText.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("Bookings"))
            {
                _done = true;
                await _cancel();
            }

            return result;
        }
    }

    [Fact]
    public async Task Sweep_Does_Not_Announce_A_Booking_That_Left_Pending_Between_Read_And_Update()
    {
        using var db = await NewDbAsync();
        var raced = Guid.NewGuid();
        var expiring = Guid.NewGuid();
        var otherListingId = Guid.NewGuid();
        await db.SeedAsync(TestData.Listing(otherListingId, OwnerId, CategoryId));
        await db.SeedAsync(
            TestData.Booking(raced, ListingId, RenterId, TestData.Today.AddDays(5), TestData.Today.AddDays(6),
                BookingStatus.Pending, expiresAt: DateTime.UtcNow.AddHours(-1)),
            TestData.Booking(expiring, otherListingId, RenterId, TestData.Today.AddDays(5), TestData.Today.AddDays(6),
                BookingStatus.Pending, expiresAt: DateTime.UtcNow.AddHours(-1)));

        var interceptor = new CancelBeforeExpireUpdateInterceptor(async () =>
        {
            await using var other = db.CreateContext();
            await other.Database.ExecuteSqlRawAsync(
                "UPDATE Bookings SET Status = {0} WHERE Id = {1}", (int)BookingStatus.Cancelled, raced);
        });

        await using (var context = db.CreateContext(interceptor))
        {
            await BookingExpiryBackgroundService.ExpireAndAnnounceAsync(
                new BookingsStore(context),
                new ChatSystemMessageEmitter(
                    new ConversationsStore(context, NullLogger<ConversationsStore>.Instance),
                    new FakeChatRealtimeNotifier(),
                    NullLogger<ChatSystemMessageEmitter>.Instance),
                DateTime.UtcNow,
                CancellationToken.None);
        }

        await using var verify = db.CreateContext();
        Assert.Equal(BookingStatus.Cancelled, verify.Bookings.Single(b => b.Id == raced).Status);
        Assert.Equal(BookingStatus.Expired, verify.Bookings.Single(b => b.Id == expiring).Status);
        var conversations = verify.Conversations.ToList();
        var expiredLines = verify.ChatMessages.Where(m => m.SystemKind == ChatSystemKind.Expired).ToList();
        var line = Assert.Single(expiredLines);
        Assert.Equal(expiring, conversations.Single(c => c.Id == line.ConversationId).BookingId);
        Assert.DoesNotContain(conversations, c => c.BookingId == raced);
    }
}
