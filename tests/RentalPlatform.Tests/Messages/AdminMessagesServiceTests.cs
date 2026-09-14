using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Messages;

// Admin console Phase 1 (moderation messages): the Messages screen backend — get-or-create
// (self/admin guards), the thread queue (search + unread/needsReply filters), and admin-role
// enforcement, mirroring AdminUsersServiceTests/AdminReportsServiceTests in shape.
public sealed class AdminMessagesServiceTests
{
    private static readonly Guid AdminId = new("f0000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherAdminId = new("f0000000-0000-0000-0000-000000000002");
    private static readonly Guid MemberId = new("f0000000-0000-0000-0000-000000000003");

    private static AdminMessagesService CreateService(AppDbContext context, Guid? currentUserId) =>
        new(
            new FakeCurrentUserContext(currentUserId),
            new AdminUsersStore(context),
            new ConversationsStore(context, NullLogger<ConversationsStore>.Instance));

    private static async Task SeedAdminAsync(SqliteTestDatabase db) =>
        await db.SeedAsync(TestData.User(AdminId, "admin@test.local", role: UserRole.Admin, isIdConfirmed: true));

    [Fact]
    public async Task OpenThread_Creates_Conversation_And_Returns_Member_Row()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        await db.SeedAsync(TestData.User(MemberId, "member@test.local", firstName: "Mira", lastName: "Member"));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).OpenThreadAsync(MemberId);

        Assert.True(result.IsSuccess);
        Assert.Equal(MemberId, result.Value!.MemberId);
        Assert.Equal("Mira", result.Value.MemberFirstName);
        Assert.Equal(UserAccountStatus.Pending, result.Value.MemberStatus);

        await using var verify = db.CreateContext();
        var conversation = await verify.Conversations.SingleAsync(c => c.RenterId == MemberId);
        Assert.Equal(ConversationKind.Moderation, conversation.Kind);
        Assert.Equal(AdminId, conversation.OwnerId);
        Assert.Equal(result.Value.ConversationId, conversation.Id);
    }

    [Fact]
    public async Task OpenThread_Is_Idempotent_When_Called_Again()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        await db.SeedAsync(TestData.User(MemberId, "member@test.local"));

        await using var firstContext = db.CreateContext();
        var first = await CreateService(firstContext, AdminId).OpenThreadAsync(MemberId);

        await using var secondContext = db.CreateContext();
        var second = await CreateService(secondContext, AdminId).OpenThreadAsync(MemberId);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(first.Value!.ConversationId, second.Value!.ConversationId);
    }

    [Fact]
    public async Task OpenThread_With_Own_Id_Is_Rejected()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).OpenThreadAsync(AdminId);

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.cannot_message_self", result.Error!.Code);
    }

    [Fact]
    public async Task OpenThread_With_Another_Admin_Is_Rejected()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        await db.SeedAsync(TestData.User(OtherAdminId, "other-admin@test.local", role: UserRole.Admin, isIdConfirmed: true));

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).OpenThreadAsync(OtherAdminId);

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.cannot_message_admin", result.Error!.Code);
    }

    [Fact]
    public async Task OpenThread_By_Non_Admin_Is_Forbidden()
    {
        using var db = new SqliteTestDatabase();
        var userId = Guid.NewGuid();
        await db.SeedAsync(TestData.User(userId, "user@test.local"));
        await db.SeedAsync(TestData.User(MemberId, "member@test.local"));

        await using var context = db.CreateContext();
        var result = await CreateService(context, userId).OpenThreadAsync(MemberId);

        Assert.False(result.IsSuccess);
        Assert.Equal("admin.forbidden", result.Error!.Code);
    }

    [Fact]
    public async Task GetThreads_Unread_Filter_Excludes_Threads_With_No_Unread_Messages()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        var readMemberId = Guid.NewGuid();
        var unreadMemberId = Guid.NewGuid();
        await db.SeedAsync(
            TestData.User(readMemberId, "read-member@test.local"),
            TestData.User(unreadMemberId, "unread-member@test.local"));

        Guid readConversationId;
        Guid unreadConversationId;
        await using (var setupContext = db.CreateContext())
        {
            var store = new ConversationsStore(setupContext, NullLogger<ConversationsStore>.Instance);
            readConversationId = (await store.GetOrCreateForModerationAsync(AdminId, readMemberId)).Id;
            unreadConversationId = (await store.GetOrCreateForModerationAsync(AdminId, unreadMemberId)).Id;

            // The member replies in both threads...
            await store.AddTextMessageAsync(readConversationId, readMemberId, "Hi");
            await store.AddTextMessageAsync(unreadConversationId, unreadMemberId, "Hi");
            // ...but only the first thread gets marked read by the moderator.
            await store.MarkReadAsync(readConversationId, AdminId);
        }

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetThreadsAsync(new AdminMessageThreadFilter { Filter = "unread" });

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value!.Items);
        Assert.Equal(unreadMemberId, item.MemberId);
        Assert.True(item.UnreadCount > 0);
    }

    [Fact]
    public async Task GetThreads_NeedsReply_Filter_Keeps_Only_Threads_Where_Member_Sent_Last()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        var memberRepliedId = Guid.NewGuid();
        var moderatorRepliedId = Guid.NewGuid();
        await db.SeedAsync(
            TestData.User(memberRepliedId, "member-replied@test.local"),
            TestData.User(moderatorRepliedId, "moderator-replied@test.local"));

        await using (var setupContext = db.CreateContext())
        {
            var store = new ConversationsStore(setupContext, NullLogger<ConversationsStore>.Instance);
            var memberRepliedConversation = await store.GetOrCreateForModerationAsync(AdminId, memberRepliedId);
            await store.AddTextMessageAsync(memberRepliedConversation.Id, memberRepliedId, "Please help");

            var moderatorRepliedConversation = await store.GetOrCreateForModerationAsync(AdminId, moderatorRepliedId);
            await store.AddTextMessageAsync(moderatorRepliedConversation.Id, moderatorRepliedId, "Please help");
            await store.AddTextMessageAsync(moderatorRepliedConversation.Id, AdminId, "We're looking into it");
        }

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetThreadsAsync(new AdminMessageThreadFilter { Filter = "needsReply" });

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value!.Items);
        Assert.Equal(memberRepliedId, item.MemberId);
        Assert.True(item.NeedsReply);
    }

    [Fact]
    public async Task GetThreads_Counts_Are_Filter_Independent_And_Match_Each_Filters_Rows()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        // Three threads: one where the member's message is unread and unanswered (counts toward
        // both Unread and NeedsReply), one read-and-answered (counts toward neither), one unread
        // because the moderator sent it but the member never read it (Unread only, NOT NeedsReply
        // since the moderator sent last).
        var unreadAndNeedsReplyId = Guid.NewGuid();
        var readAndAnsweredId = Guid.NewGuid();
        var unreadFromModeratorId = Guid.NewGuid();
        await db.SeedAsync(
            TestData.User(unreadAndNeedsReplyId, "unread-needs-reply@test.local"),
            TestData.User(readAndAnsweredId, "read-answered@test.local"),
            TestData.User(unreadFromModeratorId, "unread-from-moderator@test.local"));

        await using (var setupContext = db.CreateContext())
        {
            var store = new ConversationsStore(setupContext, NullLogger<ConversationsStore>.Instance);

            var unreadAndNeedsReplyConversation = await store.GetOrCreateForModerationAsync(AdminId, unreadAndNeedsReplyId);
            await store.AddTextMessageAsync(unreadAndNeedsReplyConversation.Id, unreadAndNeedsReplyId, "Please help");

            var readAndAnsweredConversation = await store.GetOrCreateForModerationAsync(AdminId, readAndAnsweredId);
            await store.AddTextMessageAsync(readAndAnsweredConversation.Id, readAndAnsweredId, "Please help");
            await store.AddTextMessageAsync(readAndAnsweredConversation.Id, AdminId, "We're looking into it");
            await store.MarkReadAsync(readAndAnsweredConversation.Id, AdminId);

            var unreadFromModeratorConversation = await store.GetOrCreateForModerationAsync(AdminId, unreadFromModeratorId);
            await store.AddTextMessageAsync(unreadFromModeratorConversation.Id, unreadFromModeratorId, "Please help");
            await store.AddTextMessageAsync(unreadFromModeratorConversation.Id, AdminId, "We're looking into it");
        }

        async Task<AdminMessageThreadQueueResponse> GetAsync(string filter)
        {
            await using var context = db.CreateContext();
            var result = await CreateService(context, AdminId).GetThreadsAsync(new AdminMessageThreadFilter { Filter = filter });
            Assert.True(result.IsSuccess);
            return result.Value!;
        }

        var all = await GetAsync("all");
        var unread = await GetAsync("unread");
        var needsReply = await GetAsync("needsReply");

        // Filter-independent: the same three counts regardless of which pill is active.
        Assert.Equal(3, all.Counts.All);
        Assert.Equal(2, all.Counts.Unread);
        Assert.Equal(1, all.Counts.NeedsReply);
        Assert.Equal(all.Counts.All, unread.Counts.All);
        Assert.Equal(all.Counts.Unread, unread.Counts.Unread);
        Assert.Equal(all.Counts.NeedsReply, unread.Counts.NeedsReply);
        Assert.Equal(all.Counts.All, needsReply.Counts.All);
        Assert.Equal(all.Counts.Unread, needsReply.Counts.Unread);
        Assert.Equal(all.Counts.NeedsReply, needsReply.Counts.NeedsReply);

        // Each count matches the number of rows its own filter actually returns.
        Assert.Equal(all.Counts.All, all.Items.Count);
        Assert.Equal(all.Counts.Unread, unread.Items.Count);
        Assert.Equal(all.Counts.NeedsReply, needsReply.Items.Count);
    }

    [Fact]
    public async Task GetThreads_Row_For_Moderation_Note_Exposes_Note_Type_And_Subject()
    {
        using var db = new SqliteTestDatabase();
        await SeedAdminAsync(db);
        await db.SeedAsync(TestData.User(MemberId, "member@test.local"));

        Guid conversationId;
        await using (var setupContext = db.CreateContext())
        {
            var store = new ConversationsStore(setupContext, NullLogger<ConversationsStore>.Instance);
            var conversation = await store.GetOrCreateForModerationAsync(AdminId, MemberId);
            conversationId = conversation.Id;
            await store.AddModerationNoteAsync(
                conversationId,
                AdminId,
                ModerationNoteKind.Reject,
                subject: "Wooden Train Set",
                reason: "Photos too blurry",
                body: "Your listing \"Wooden Train Set\" was rejected: Photos too blurry.");
        }

        await using var context = db.CreateContext();
        var result = await CreateService(context, AdminId).GetThreadsAsync(new AdminMessageThreadFilter { Filter = "all" });

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value!.Items);
        Assert.Equal("moderationNote", item.LastMessageType);
        Assert.Equal("Wooden Train Set", item.LastMessageNoteSubject);
    }
}
