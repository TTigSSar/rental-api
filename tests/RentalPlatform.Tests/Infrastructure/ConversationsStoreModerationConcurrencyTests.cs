using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// Covers the get-or-create race fixed by migration DeduplicateModerationConversations:
// ConversationsStore.GetOrCreateForModerationAsync used check-then-insert with no unique
// constraint backing it, so two concurrent calls for the same member could both insert a
// Moderation conversation. These tests need a real SQL Server database (SqlServerTestDatabase,
// migrated through the real EF migrations) rather than the SqliteTestDatabase the rest of the
// suite uses, because the fix depends on two things SQLite cannot reproduce:
//   - genuine concurrent writers (SQLite serialises commands on its single shared connection);
//   - the exact Microsoft.Data.SqlClient.SqlException unique-violation numbers (2601/2627) the
//     store's catch keys off — SQLite raises SqliteException instead, which that catch does not
//     (and must not) match.
//
// Requires a local SQL Server reachable at "Server=.;Trusted_Connection=True" (same instance
// CLAUDE.md's "Running locally" section assumes for RentalPlatformDbDev).
public sealed class ConversationsStoreModerationConcurrencyTests
{
    // The migration immediately before DeduplicateModerationConversations — no column changes
    // happen in the migration under test, so seeding through today's entity model against this
    // older schema state is safe and lets the consolidation test seed real pre-existing
    // duplicates the way the bug actually produced them.
    private const string MigrationBeforeDeduplication = "20260814141003_AddConversationModeration";

    private static ConversationsStore CreateStore(AppDbContext context) =>
        new(context, NullLogger<ConversationsStore>.Instance);

    // The point of the whole task: a genuine race, not a sequential simulation. Several tasks,
    // each with its own AppDbContext/connection, are released through a shared gate so they hit
    // GetOrCreateForModerationAsync's check-then-insert as close to simultaneously as real SQL
    // Server round-trip latency allows. Exactly one Conversation must survive and every caller
    // must receive that same ConversationId.
    [Fact]
    public async Task GetOrCreateForModerationAsync_ConcurrentCallers_Converge_On_One_Conversation()
    {
        using var db = new SqlServerTestDatabase();

        var memberId = Guid.NewGuid();
        var moderatorIds = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        await db.SeedAsync(new object[] { TestData.User(memberId, "member@race.test") }
            .Concat(moderatorIds.Select((id, i) => TestData.User(id, $"moderator{i}@race.test", role: UserRole.Admin)))
            .ToArray());

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var racers = moderatorIds.Select(moderatorId => Task.Run(async () =>
        {
            await gate.Task;
            await using var context = db.CreateContext();
            return await CreateStore(context).GetOrCreateForModerationAsync(moderatorId, memberId);
        })).ToArray();

        // Give every task a moment to reach "await gate.Task" and block there, then release them
        // all at once so their check-then-insert windows genuinely overlap.
        await Task.Delay(50);
        gate.SetResult();

        var conversations = await Task.WhenAll(racers);

        var distinctIds = conversations.Select(c => c.Id).Distinct().ToList();
        Assert.True(
            distinctIds.Count == 1,
            $"Expected every concurrent caller to converge on one conversation, got {distinctIds.Count}: {string.Join(", ", distinctIds)}");

        await using var verify = db.CreateContext();
        var storedCount = await verify.Conversations
            .CountAsync(c => c.Kind == ConversationKind.Moderation && c.RenterId == memberId);
        Assert.Equal(1, storedCount);
    }

    // Directly exercises the migration's consolidation logic against pre-existing duplicates —
    // the shape the bug actually left behind before this fix. Seeds two duplicate Moderation
    // threads (against the schema as it stood right before the new unique index), each with a
    // message and a participant, then applies the migration and asserts zero message loss: both
    // messages survive, re-pointed onto the single surviving (earliest-opened) conversation, and
    // both distinct moderator participants are preserved while the duplicated member participant
    // row is not.
    [Fact]
    public async Task DeduplicateModerationConversations_Migration_Consolidates_Duplicates_Without_Losing_Messages()
    {
        using var db = new SqlServerTestDatabase();
        db.MigrateTo(MigrationBeforeDeduplication);

        var memberId = Guid.NewGuid();
        var moderatorOneId = Guid.NewGuid();
        var moderatorTwoId = Guid.NewGuid();

        var keeperId = Guid.NewGuid();
        var loserId = Guid.NewGuid();
        var baseTime = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

        // Thread A: opened first by moderatorOne — expected to survive (earliest CreatedAt).
        var keeperConversation = new Conversation
        {
            Id = keeperId,
            BookingId = null,
            Kind = ConversationKind.Moderation,
            OwnerId = moderatorOneId,
            RenterId = memberId,
            CreatedAt = baseTime
        };
        var keeperMessage = new ChatMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = keeperId,
            SenderId = moderatorOneId,
            Type = MessageType.Text,
            Body = "Thread A: first message",
            CreatedAt = baseTime
        };
        keeperConversation.LastMessageId = keeperMessage.Id;
        keeperConversation.LastMessageSnippet = keeperMessage.Body;
        keeperConversation.LastMessageAt = keeperMessage.CreatedAt;

        // Thread B: opened later by moderatorTwo — the duplicate; its message is the more recent
        // one, so the keeper's denormalised last-message preview must move onto it after merge.
        var loserConversation = new Conversation
        {
            Id = loserId,
            BookingId = null,
            Kind = ConversationKind.Moderation,
            OwnerId = moderatorTwoId,
            RenterId = memberId,
            CreatedAt = baseTime.AddMinutes(5)
        };
        var loserMessage = new ChatMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = loserId,
            SenderId = moderatorTwoId,
            Type = MessageType.Text,
            Body = "Thread B: more recent message",
            CreatedAt = baseTime.AddMinutes(10)
        };
        loserConversation.LastMessageId = loserMessage.Id;
        loserConversation.LastMessageSnippet = loserMessage.Body;
        loserConversation.LastMessageAt = loserMessage.CreatedAt;

        await db.SeedAsync(
            TestData.User(memberId, "member@dedupe.test"),
            TestData.User(moderatorOneId, "moderator-one@dedupe.test", role: UserRole.Admin),
            TestData.User(moderatorTwoId, "moderator-two@dedupe.test", role: UserRole.Admin));

        await db.SeedAsync(
            keeperConversation,
            loserConversation,
            keeperMessage,
            loserMessage,
            new ConversationParticipant { Id = Guid.NewGuid(), ConversationId = keeperId, UserId = moderatorOneId },
            // Same member participant shape on both threads — exercises the "already present on
            // the keeper, drop the duplicate" branch.
            new ConversationParticipant { Id = Guid.NewGuid(), ConversationId = keeperId, UserId = memberId },
            new ConversationParticipant { Id = Guid.NewGuid(), ConversationId = loserId, UserId = moderatorTwoId },
            new ConversationParticipant { Id = Guid.NewGuid(), ConversationId = loserId, UserId = memberId });

        db.MigrateToLatest();

        await using var verify = db.CreateContext();

        var survivors = await verify.Conversations
            .Where(c => c.Kind == ConversationKind.Moderation && c.RenterId == memberId)
            .ToListAsync();
        var survivor = Assert.Single(survivors);
        Assert.Equal(keeperId, survivor.Id);

        // Zero message loss: both original messages still exist, both now pointing at the
        // survivor.
        var messages = await verify.ChatMessages
            .Where(m => m.Id == keeperMessage.Id || m.Id == loserMessage.Id)
            .ToListAsync();
        Assert.Equal(2, messages.Count);
        Assert.All(messages, m => Assert.Equal(keeperId, m.ConversationId));

        // Denormalised last-message preview reflects the actually-most-recent message across
        // both merged threads (the loser's), not just whatever the keeper had before the merge.
        Assert.Equal(loserMessage.Id, survivor.LastMessageId);
        Assert.Equal(loserMessage.Body, survivor.LastMessageSnippet);
        Assert.Equal(loserMessage.CreatedAt, survivor.LastMessageAt);

        // Participants: moderatorOne's and the member's rows already existed on the keeper and
        // are untouched; moderatorTwo's row was re-pointed from the loser; the loser's duplicate
        // member row was dropped rather than colliding with the keeper's.
        var participants = await verify.ConversationParticipants
            .Where(p => p.ConversationId == keeperId)
            .ToListAsync();
        Assert.Equal(3, participants.Count);
        Assert.Contains(participants, p => p.UserId == moderatorOneId);
        Assert.Contains(participants, p => p.UserId == moderatorTwoId);
        Assert.Contains(participants, p => p.UserId == memberId);
        Assert.Equal(1, participants.Count(p => p.UserId == memberId));

        // The loser row itself is gone.
        Assert.False(await verify.Conversations.AnyAsync(c => c.Id == loserId));
        Assert.False(await verify.ConversationParticipants.AnyAsync(p => p.ConversationId == loserId));
    }
}
