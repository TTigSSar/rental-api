using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using System.Threading;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// Admin console Phase 1 (moderation messages): ConversationsStore's booking-less Moderation
// thread support — the filtered-unique-index case (BookingId stays null for every Moderation
// row, so a plain unique index would reject the second one) and get-or-create idempotency
// shared across moderators (one thread per member, not per moderator).
public sealed class ConversationsStoreModerationTests
{
    private static ConversationsStore CreateStore(AppDbContext context) =>
        new(context, NullLogger<ConversationsStore>.Instance);

    [Fact]
    public async Task GetOrCreateForModerationAsync_Creates_Conversation_With_Null_BookingId_And_Moderation_Kind()
    {
        using var db = new SqliteTestDatabase();
        var moderatorId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        await db.SeedAsync(
            TestData.User(moderatorId, "moderator@test.local", role: UserRole.Admin),
            TestData.User(memberId, "member@test.local"));

        await using var context = db.CreateContext();
        var conversation = await CreateStore(context).GetOrCreateForModerationAsync(moderatorId, memberId);

        Assert.Null(conversation.BookingId);
        Assert.Equal(ConversationKind.Moderation, conversation.Kind);
        Assert.Equal(moderatorId, conversation.OwnerId);
        Assert.Equal(memberId, conversation.RenterId);

        await using var verify = db.CreateContext();
        var stored = await verify.Conversations.SingleAsync(c => c.Id == conversation.Id);
        Assert.Null(stored.BookingId);
        var participants = await verify.ConversationParticipants
            .Where(p => p.ConversationId == conversation.Id)
            .ToListAsync();
        Assert.Equal(2, participants.Count);
        Assert.Contains(participants, p => p.UserId == moderatorId);
        Assert.Contains(participants, p => p.UserId == memberId);
    }

    // The filtered-unique-index case: SQL Server's plain unique index permits only one NULL row,
    // so without ConversationConfiguration's HasFilter("[BookingId] IS NOT NULL"), the second
    // Moderation thread (also BookingId == null) would fail to insert. Two different members must
    // both succeed.
    [Fact]
    public async Task GetOrCreateForModerationAsync_Allows_Two_Different_Members_To_Both_Insert()
    {
        using var db = new SqliteTestDatabase();
        var moderatorId = Guid.NewGuid();
        var memberOneId = Guid.NewGuid();
        var memberTwoId = Guid.NewGuid();
        await db.SeedAsync(
            TestData.User(moderatorId, "moderator@test.local", role: UserRole.Admin),
            TestData.User(memberOneId, "member-one@test.local"),
            TestData.User(memberTwoId, "member-two@test.local"));

        await using var contextOne = db.CreateContext();
        var conversationOne = await CreateStore(contextOne).GetOrCreateForModerationAsync(moderatorId, memberOneId);

        await using var contextTwo = db.CreateContext();
        var conversationTwo = await CreateStore(contextTwo).GetOrCreateForModerationAsync(moderatorId, memberTwoId);

        Assert.NotEqual(conversationOne.Id, conversationTwo.Id);

        await using var verify = db.CreateContext();
        Assert.Equal(2, await verify.Conversations.CountAsync(c => c.Kind == ConversationKind.Moderation));
    }

    // One thread per member, not per moderator: a second moderator opening the same member's
    // thread must get back the same conversation, not a duplicate.
    [Fact]
    public async Task GetOrCreateForModerationAsync_Is_Idempotent_And_Shared_Across_Moderators()
    {
        using var db = new SqliteTestDatabase();
        var moderatorOneId = Guid.NewGuid();
        var moderatorTwoId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        await db.SeedAsync(
            TestData.User(moderatorOneId, "moderator-one@test.local", role: UserRole.Admin),
            TestData.User(moderatorTwoId, "moderator-two@test.local", role: UserRole.Admin),
            TestData.User(memberId, "member@test.local"));

        await using var firstContext = db.CreateContext();
        var first = await CreateStore(firstContext).GetOrCreateForModerationAsync(moderatorOneId, memberId);

        await using var secondContext = db.CreateContext();
        var second = await CreateStore(secondContext).GetOrCreateForModerationAsync(moderatorTwoId, memberId);

        Assert.Equal(first.Id, second.Id);
        // OwnerId records whoever opened it (the first moderator) — the second moderator's call
        // continues the same thread rather than reassigning ownership.
        Assert.Equal(moderatorOneId, second.OwnerId);

        await using var verify = db.CreateContext();
        Assert.Equal(1, await verify.Conversations.CountAsync(c => c.Kind == ConversationKind.Moderation && c.RenterId == memberId));
    }

    // The new IX_Conversations_Kind_RenterId unique filtered index (filter: [Kind] = 1) must
    // constrain Moderation rows only — a Booking conversation (Kind = 0) for the very same
    // RenterId value must still insert fine alongside a Moderation thread for that user.
    [Fact]
    public async Task GetOrCreateForModerationAsync_Coexists_With_A_Booking_Conversation_For_The_Same_User()
    {
        using var db = new SqliteTestDatabase();
        var ownerId = Guid.NewGuid();
        var renterId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        await db.SeedAsync(
            TestData.User(ownerId, "owner@test.local", role: UserRole.Admin),
            TestData.User(renterId, "renter@test.local"),
            TestData.Category(categoryId),
            TestData.Listing(listingId, ownerId, categoryId),
            TestData.Booking(bookingId, listingId, renterId, TestData.Today, TestData.Today.AddDays(3), BookingStatus.Approved));

        await using var bookingContext = db.CreateContext();
        var bookingConversation = await CreateStore(bookingContext)
            .GetOrCreateForBookingAsync(bookingId, renterId);
        Assert.NotNull(bookingConversation);

        // ownerId doubles as the moderator here — renterId is the RenterId value on both rows,
        // which is exactly the case the filter must not trip over.
        await using var moderationContext = db.CreateContext();
        var moderationConversation = await CreateStore(moderationContext)
            .GetOrCreateForModerationAsync(ownerId, renterId);

        Assert.NotEqual(bookingConversation!.Id, moderationConversation.Id);

        await using var verify = db.CreateContext();
        Assert.Equal(2, await verify.Conversations.CountAsync(c => c.RenterId == renterId));
        Assert.Equal(
            1, await verify.Conversations.CountAsync(c => c.RenterId == renterId && c.Kind == ConversationKind.Booking));
        Assert.Equal(
            1, await verify.Conversations.CountAsync(c => c.RenterId == renterId && c.Kind == ConversationKind.Moderation));
    }

    // Genuine concurrency: fires two GetOrCreateForModerationAsync calls for the same member truly
    // in parallel, on two separate SqliteConnections against a SQLite shared-cache in-memory
    // database (Cache=Shared) — unlike every other test in this file (which shares one
    // SqliteTestDatabase connection object: fine for sequential seed/act/verify, but that
    // connection has no locking of its own and isn't safe for two real OS threads to hit at once).
    // Microsoft.Data.Sqlite's default 30s busy timeout serializes the two writers at the SQLite
    // engine level (rather than throwing SQLITE_BUSY), so whichever call's INSERT loses the race
    // genuinely trips IX_Conversations_Kind_RenterId's unique constraint — the same race two
    // moderators double-clicking "Message" on the same member, or two quick listing rejections via
    // ModerationNoteEmitter, could hit in production.
    //
    // ConversationsStore.IsUniqueConstraintViolation recognises only SQL Server's SqlException
    // (2601/2627) — the real production shape (mirrors FavoritesStore.TryAddAsync) — because
    // production never talks to SQLite. This test suite runs on SQLite (SqliteTestDatabase), whose
    // own unique-constraint violation is a different exception type the store deliberately does
    // not special-case. So the loser's raw DbUpdateException surfacing here is itself proof the
    // schema-level protection this fix adds is real; the local catch below performs the identical
    // "re-read and return the winner's row" recovery the task requires, standing in for what
    // ConversationsStore's own catch does for the SQL-Server-shaped exception in production.
    [Fact]
    public async Task GetOrCreateForModerationAsync_Concurrent_Calls_For_Same_Member_Yield_One_Conversation()
    {
        var connectionString = $"Data Source=modtest-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

        // A shared-cache in-memory database is dropped once its last connection closes — keep one
        // open for the lifetime of the test.
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        await using (var schemaContext = CreateSharedCacheContext(connectionString))
        {
            await schemaContext.Database.EnsureCreatedAsync();
        }

        var moderatorId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        await using (var seedContext = CreateSharedCacheContext(connectionString))
        {
            seedContext.AddRange(
                TestData.User(moderatorId, "moderator@test.local", role: UserRole.Admin),
                TestData.User(memberId, "member@test.local"));
            await seedContext.SaveChangesAsync();
        }

        using var barrier = new Barrier(2);

        Task<Conversation> RunAsync() => Task.Run(async () =>
        {
            await using var context = CreateSharedCacheContext(connectionString);
            var store = CreateStore(context);
            // Lines both threads up so their SELECT-then-INSERT windows genuinely overlap instead
            // of one call completing (and committing) before the other even starts.
            barrier.SignalAndWait();
            try
            {
                return await store.GetOrCreateForModerationAsync(moderatorId, memberId);
            }
            catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteErrorCode: 19 })
            {
                await using var reread = CreateSharedCacheContext(connectionString);
                return await reread.Conversations.FirstAsync(
                    c => c.Kind == ConversationKind.Moderation && c.RenterId == memberId);
            }
        });

        var results = await Task.WhenAll(RunAsync(), RunAsync());

        Assert.Equal(results[0].Id, results[1].Id);

        await using var verify = CreateSharedCacheContext(connectionString);
        Assert.Equal(
            1,
            await verify.Conversations.CountAsync(c => c.Kind == ConversationKind.Moderation && c.RenterId == memberId));
    }

    private static AppDbContext CreateSharedCacheContext(string connectionString) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options);

    [Fact]
    public async Task AddModerationNoteAsync_Has_No_Idempotency_Guard_Two_Notes_Of_Same_Kind_Both_Insert()
    {
        using var db = new SqliteTestDatabase();
        var moderatorId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        await db.SeedAsync(
            TestData.User(moderatorId, "moderator@test.local", role: UserRole.Admin),
            TestData.User(memberId, "member@test.local"));

        await using var context = db.CreateContext();
        var store = CreateStore(context);
        var conversation = await store.GetOrCreateForModerationAsync(moderatorId, memberId);

        await store.AddModerationNoteAsync(
            conversation.Id, moderatorId, ModerationNoteKind.Reject, "Listing A", "Poor images", "Rejected: Listing A");
        await store.AddModerationNoteAsync(
            conversation.Id, moderatorId, ModerationNoteKind.Reject, "Listing B", "Poor images", "Rejected: Listing B");

        await using var verify = db.CreateContext();
        var noteCount = await verify.ChatMessages.CountAsync(
            m => m.ConversationId == conversation.Id && m.Type == RentalPlatform.Domain.Enums.MessageType.ModerationNote);
        Assert.Equal(2, noteCount);
    }
}
