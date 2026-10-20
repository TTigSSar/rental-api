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
}
