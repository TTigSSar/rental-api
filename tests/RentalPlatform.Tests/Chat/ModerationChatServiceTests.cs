using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Chat;

// Admin console Phase 1 (moderation messages): the booking-less Conversation.Kind == Moderation
// path through the existing /api/chat surface (ChatService), reused unchanged by the admin UI —
// a Moderation thread must show up in the member's own inbox, and the chat.user_blocked guard
// must carve out exactly the suspended member replying in their own Moderation thread, never a
// Booking thread.
public sealed class ModerationChatServiceTests
{
    private static readonly Guid ModeratorId = new("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid MemberId = new("c0000000-0000-0000-0000-000000000002");
    private static readonly Guid SecondModeratorId = new("c0000000-0000-0000-0000-000000000008");
    private static readonly Guid ThirdPartyId = new("c0000000-0000-0000-0000-000000000009");

    private static ChatService CreateService(
        AppDbContext context,
        Guid currentUserId,
        FakeChatRealtimeNotifier? realtimeNotifier = null) =>
        new(
            new FakeCurrentUserContext(currentUserId),
            new ConversationsStore(context, NullLogger<ConversationsStore>.Instance),
            new BookingsStore(context),
            new ReviewsStore(context),
            realtimeNotifier ?? new FakeChatRealtimeNotifier(),
            new FakeFileStorageService());

    [Fact]
    public async Task Moderation_Thread_Appears_In_Members_Own_Conversation_List()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(
            TestData.User(ModeratorId, "moderator@test.local", role: UserRole.Admin),
            TestData.User(MemberId, "member@test.local"));

        await using (var setupContext = db.CreateContext())
        {
            var store = new ConversationsStore(setupContext, NullLogger<ConversationsStore>.Instance);
            await store.GetOrCreateForModerationAsync(ModeratorId, MemberId);
        }

        await using var context = db.CreateContext();
        var result = await CreateService(context, MemberId).GetConversationsAsync();

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value!);
        Assert.Equal("moderation", item.Kind);
        Assert.Null(item.BookingId);
        Assert.Null(item.ToyTitle);
        Assert.Equal("moderation", item.Status);
    }

    // Confirms GetDetailsAsync's Include(Booking) is a LEFT JOIN, not an INNER JOIN, now that
    // Conversation.BookingId is optional: if EF still emitted an inner join here, this call would
    // return null (no matching Booking row) instead of a details response with Booking == null.
    [Fact]
    public async Task Moderation_Thread_Details_Load_Without_A_Booking()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(
            TestData.User(ModeratorId, "moderator@test.local", role: UserRole.Admin),
            TestData.User(MemberId, "member@test.local"));

        Guid conversationId;
        await using (var setupContext = db.CreateContext())
        {
            var store = new ConversationsStore(setupContext, NullLogger<ConversationsStore>.Instance);
            conversationId = (await store.GetOrCreateForModerationAsync(ModeratorId, MemberId)).Id;
        }

        await using var context = db.CreateContext();
        var result = await CreateService(context, MemberId).GetConversationAsync(conversationId, page: null, pageSize: null);

        Assert.True(result.IsSuccess);
        Assert.Equal("moderation", result.Value!.Kind);
        Assert.Null(result.Value.BookingId);
        Assert.Null(result.Value.BookingDates);
        Assert.Null(result.Value.BookingPrice);
        Assert.Equal("moderation", result.Value.Status);
    }

    [Fact]
    public async Task Suspended_Member_Can_Send_In_Own_Moderation_Thread()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(
            TestData.User(ModeratorId, "moderator@test.local", role: UserRole.Admin),
            TestData.User(MemberId, "member@test.local", isBlocked: true));

        Guid conversationId;
        await using (var setupContext = db.CreateContext())
        {
            var store = new ConversationsStore(setupContext, NullLogger<ConversationsStore>.Instance);
            var conversation = await store.GetOrCreateForModerationAsync(ModeratorId, MemberId);
            conversationId = conversation.Id;
        }

        await using var context = db.CreateContext();
        var result = await CreateService(context, MemberId).SendMessageAsync(new SendChatMessageRequest
        {
            ConversationId = conversationId,
            Content = "Can you please review my listing again?"
        });

        Assert.True(result.IsSuccess);

        await using var verify = db.CreateContext();
        Assert.Equal(1, await verify.ChatMessages.CountAsync());
    }

    [Fact]
    public async Task Suspended_Member_Still_Blocked_In_A_Booking_Thread()
    {
        using var db = new SqliteTestDatabase();
        var ownerId = new Guid("c0000000-0000-0000-0000-000000000003");
        var categoryId = new Guid("c0000000-0000-0000-0000-000000000004");
        var listingId = new Guid("c0000000-0000-0000-0000-000000000005");
        var bookingId = new Guid("c0000000-0000-0000-0000-000000000006");
        var conversationId = new Guid("c0000000-0000-0000-0000-000000000007");
        var today = TestData.Today;

        await db.SeedAsync(
            TestData.User(ownerId, "owner@test.local"),
            TestData.User(MemberId, "member@test.local", isBlocked: true),
            TestData.Category(categoryId));
        await db.SeedAsync(TestData.Listing(listingId, ownerId, categoryId));
        await db.SeedAsync(TestData.Booking(
            bookingId, listingId, MemberId, today.AddDays(5), today.AddDays(8), BookingStatus.Approved));
        await db.SeedAsync(TestData.Conversation(conversationId, bookingId, ownerId, MemberId));

        await using var context = db.CreateContext();
        var result = await CreateService(context, MemberId).SendMessageAsync(new SendChatMessageRequest
        {
            ConversationId = conversationId,
            Content = "Hello"
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("chat.user_blocked", result.Error!.Code);
    }

    // Follow-up fix: the admin console's Messages screen is a SHARED inbox — a second moderator
    // who didn't open the thread (so isn't its OwnerId) must still be able to open, reply to, and
    // mark read a thread another moderator started. Any Admin is a participant on a Moderation
    // thread; only Booking threads keep the strict OwnerId/RenterId rule.
    [Fact]
    public async Task Second_Moderator_Can_Open_Send_And_Mark_Read_A_Thread_Another_Moderator_Opened()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(
            TestData.User(ModeratorId, "moderator@test.local", role: UserRole.Admin),
            TestData.User(SecondModeratorId, "second-moderator@test.local", role: UserRole.Admin),
            TestData.User(MemberId, "member@test.local"));

        Guid conversationId;
        await using (var setupContext = db.CreateContext())
        {
            var store = new ConversationsStore(setupContext, NullLogger<ConversationsStore>.Instance);
            // Opened by ModeratorId — OwnerId is pinned to them, never SecondModeratorId.
            conversationId = (await store.GetOrCreateForModerationAsync(ModeratorId, MemberId)).Id;
        }

        // Open (detail view).
        await using (var detailContext = db.CreateContext())
        {
            var detailResult = await CreateService(detailContext, SecondModeratorId)
                .GetConversationAsync(conversationId, page: null, pageSize: null);
            Assert.True(detailResult.IsSuccess);
        }

        // Send.
        await using (var sendContext = db.CreateContext())
        {
            var sendResult = await CreateService(sendContext, SecondModeratorId).SendMessageAsync(new SendChatMessageRequest
            {
                ConversationId = conversationId,
                Content = "This is Sona, taking over from here."
            });
            Assert.True(sendResult.IsSuccess);
        }

        // Mark read.
        await using (var readContext = db.CreateContext())
        {
            var readResult = await CreateService(readContext, SecondModeratorId).MarkReadAsync(conversationId);
            Assert.True(readResult.IsSuccess);
        }
    }

    // The widened rule must stay scoped to Admins — a random authenticated user who is neither
    // the member nor an admin must still be rejected on someone else's Moderation thread.
    [Fact]
    public async Task Non_Admin_Third_Party_Still_Blocked_From_Someone_Elses_Moderation_Thread()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(
            TestData.User(ModeratorId, "moderator@test.local", role: UserRole.Admin),
            TestData.User(MemberId, "member@test.local"),
            TestData.User(ThirdPartyId, "third-party@test.local"));

        Guid conversationId;
        await using (var setupContext = db.CreateContext())
        {
            var store = new ConversationsStore(setupContext, NullLogger<ConversationsStore>.Instance);
            conversationId = (await store.GetOrCreateForModerationAsync(ModeratorId, MemberId)).Id;
        }

        await using var detailContext = db.CreateContext();
        var detailResult = await CreateService(detailContext, ThirdPartyId)
            .GetConversationAsync(conversationId, page: null, pageSize: null);
        Assert.False(detailResult.IsSuccess);
        Assert.Equal("chat.not_participant", detailResult.Error!.Code);

        await using var sendContext = db.CreateContext();
        var sendResult = await CreateService(sendContext, ThirdPartyId).SendMessageAsync(new SendChatMessageRequest
        {
            ConversationId = conversationId,
            Content = "I shouldn't be able to send this."
        });
        Assert.False(sendResult.IsSuccess);
        Assert.Equal("chat.not_participant", sendResult.Error!.Code);

        await using var readContext = db.CreateContext();
        var readResult = await CreateService(readContext, ThirdPartyId).MarkReadAsync(conversationId);
        Assert.False(readResult.IsSuccess);
        Assert.Equal("chat.not_participant", readResult.Error!.Code);

        await using var verify = db.CreateContext();
        Assert.Equal(0, await verify.ChatMessages.CountAsync());
    }

    // Booking threads keep the exact OwnerId/RenterId rule: an Admin who is neither the booking's
    // owner nor its renter must still be rejected — the widened rule is Moderation-only.
    [Fact]
    public async Task Admin_Is_Still_Not_A_Participant_On_A_Booking_Thread_They_Are_Not_Party_To()
    {
        using var db = new SqliteTestDatabase();
        var ownerId = new Guid("c0000000-0000-0000-0000-000000000010");
        var renterId = new Guid("c0000000-0000-0000-0000-000000000011");
        var categoryId = new Guid("c0000000-0000-0000-0000-000000000012");
        var listingId = new Guid("c0000000-0000-0000-0000-000000000013");
        var bookingId = new Guid("c0000000-0000-0000-0000-000000000014");
        var conversationId = new Guid("c0000000-0000-0000-0000-000000000015");
        var today = TestData.Today;

        await db.SeedAsync(
            TestData.User(ownerId, "booking-owner@test.local"),
            TestData.User(renterId, "booking-renter@test.local"),
            TestData.User(ModeratorId, "moderator@test.local", role: UserRole.Admin),
            TestData.Category(categoryId));
        await db.SeedAsync(TestData.Listing(listingId, ownerId, categoryId));
        await db.SeedAsync(TestData.Booking(
            bookingId, listingId, renterId, today.AddDays(5), today.AddDays(8), BookingStatus.Approved));
        await db.SeedAsync(TestData.Conversation(conversationId, bookingId, ownerId, renterId));

        await using var context = db.CreateContext();
        var result = await CreateService(context, ModeratorId)
            .GetConversationAsync(conversationId, page: null, pageSize: null);

        Assert.False(result.IsSuccess);
        Assert.Equal("chat.not_participant", result.Error!.Code);
    }
}
