using System.Text.Json;
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

// Owner-shared "meeting point" chat message (Location). Real ChatService over the real stores on
// an isolated in-memory SQLite database.
public sealed class MeetingPointTests
{
    private static readonly Guid OwnerId = new("e0000000-0000-0000-0000-000000000001");
    private static readonly Guid RenterId = new("e0000000-0000-0000-0000-000000000002");
    private static readonly Guid OutsiderId = new("e0000000-0000-0000-0000-000000000003");
    private static readonly Guid CategoryId = new("e0000000-0000-0000-0000-000000000004");
    private static readonly Guid ListingId = new("e0000000-0000-0000-0000-000000000005");
    private static readonly Guid BookingId = new("e0000000-0000-0000-0000-000000000006");
    private static readonly Guid ConversationId = new("e0000000-0000-0000-0000-000000000007");

    private static readonly DateOnly Today = TestData.Today;

    private static async Task SeedAsync(
        SqliteTestDatabase db,
        BookingStatus status = BookingStatus.Approved,
        bool ownerHasHome = true)
    {
        var owner = ownerHasHome
            ? TestData.OwnerWithHome(OwnerId, "owner@test.local", (40.185612m, 44.512634m))
            : TestData.User(OwnerId, "owner@test.local");

        await db.SeedAsync(
            owner,
            TestData.User(RenterId, "renter@test.local"),
            TestData.User(OutsiderId, "outsider@test.local"),
            TestData.Category(CategoryId));
        await db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId));
        await db.SeedAsync(TestData.Booking(BookingId, ListingId, RenterId, Today.AddDays(5), Today.AddDays(8), status));
        await db.SeedAsync(TestData.Conversation(ConversationId, BookingId, OwnerId, RenterId));
    }

    private static ChatService CreateService(
        AppDbContext context, Guid currentUserId, FakeChatRealtimeNotifier? notifier = null) =>
        new(
            new FakeCurrentUserContext(currentUserId),
            new ConversationsStore(context, NullLogger<ConversationsStore>.Instance),
            new BookingsStore(context),
            new ReviewsStore(context),
            notifier ?? new FakeChatRealtimeNotifier(),
            new FakeFileStorageService(),
            new DistrictBoundaryProvider());

    private static ShareMeetingPointRequest Other(decimal? lat, decimal? lng) =>
        new() { Kind = "other", Latitude = lat, Longitude = lng };

    [Fact]
    public async Task Owner_Shares_Home_Stores_And_Returns_Exact_Home_Pair_Ignoring_Body_Coordinates()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);
        var notifier = new FakeChatRealtimeNotifier();

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId, notifier).ShareMeetingPointAsync(
            ConversationId, new ShareMeetingPointRequest { Kind = "home", Latitude = 1m, Longitude = 2m });

        Assert.True(result.IsSuccess);
        var message = result.Value!;
        Assert.Equal("location", message.Type);
        Assert.Equal("home", message.MeetingPointKind);
        Assert.Equal(40.185612m, message.Latitude);
        Assert.Equal(44.512634m, message.Longitude);
        Assert.Null(message.Body);
        Assert.Null(message.AttachmentUrl);

        await using var read = db.CreateContext();
        var stored = await read.ChatMessages.SingleAsync();
        Assert.Equal(MessageType.Location, stored.Type);
        Assert.Equal(MeetingPointKind.Home, stored.MeetingPointKind);
        Assert.Equal(40.185612m, stored.MeetingPointLatitude);
        Assert.Equal(44.512634m, stored.MeetingPointLongitude);

        var broadcast = Assert.Single(notifier.MessageSentCalls);
        Assert.Equal("location", broadcast.Message.Type);
        Assert.Equal(40.185612m, broadcast.Message.Latitude);

        var conversation = await read.Conversations.SingleAsync();
        Assert.Equal(stored.Id, conversation.LastMessageId);
        Assert.Null(conversation.LastMessageSnippet);
        Assert.NotNull(conversation.LastMessageAt);
    }

    [Fact]
    public async Task Owner_Shares_Other_In_Yerevan_Rounds_To_Six_Decimals()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).ShareMeetingPointAsync(
            ConversationId, Other(40.2010123456m, 44.5090123456m));

        Assert.True(result.IsSuccess);
        Assert.Equal("other", result.Value!.MeetingPointKind);
        Assert.Equal(40.201012m, result.Value.Latitude);
        Assert.Equal(44.509012m, result.Value.Longitude);
    }

    [Fact]
    public async Task Other_Outside_Yerevan_Is_Refused()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);

        await using var context = db.CreateContext();
        var point = TestData.OutsideYerevanPoint;
        var result = await CreateService(context, OwnerId).ShareMeetingPointAsync(
            ConversationId, Other(point.Latitude, point.Longitude));

        Assert.False(result.IsSuccess);
        Assert.Equal("chat.meeting_point_outside_yerevan", result.Error!.Code);
        Assert.Empty(await db.CreateContext().ChatMessages.ToListAsync());
    }

    [Theory]
    [InlineData(null, 44.5)]
    [InlineData(40.2, null)]
    [InlineData(95.0, 44.5)]
    public async Task Other_With_Missing_Or_Invalid_Coordinates_Is_Refused(double? lat, double? lng)
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).ShareMeetingPointAsync(
            ConversationId, Other((decimal?)lat, (decimal?)lng));

        Assert.False(result.IsSuccess);
        Assert.Equal("chat.meeting_point_invalid", result.Error!.Code);
    }

    [Fact]
    public async Task Unknown_Kind_Is_Refused()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).ShareMeetingPointAsync(
            ConversationId, new ShareMeetingPointRequest { Kind = "elsewhere" });

        Assert.Equal("chat.meeting_point_invalid", result.Error!.Code);
    }

    [Fact]
    public async Task Home_Without_Home_Point_Is_Refused()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, ownerHasHome: false);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).ShareMeetingPointAsync(
            ConversationId, new ShareMeetingPointRequest { Kind = "home" });

        Assert.Equal("chat.home_point_required", result.Error!.Code);
    }

    [Fact]
    public async Task Renter_Cannot_Share()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, RenterId).ShareMeetingPointAsync(
            ConversationId, new ShareMeetingPointRequest { Kind = "home" });

        Assert.Equal("chat.meeting_point_owner_only", result.Error!.Code);
    }

    [Fact]
    public async Task Blocked_Owner_Cannot_Share()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);
        await using (var ctx = db.CreateContext())
        {
            (await ctx.Users.SingleAsync(u => u.Id == OwnerId)).IsBlocked = true;
            await ctx.SaveChangesAsync();
        }

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).ShareMeetingPointAsync(
            ConversationId, new ShareMeetingPointRequest { Kind = "home" });

        Assert.False(result.IsSuccess);
        Assert.Equal("chat.user_blocked", result.Error!.Code);
        Assert.Empty(await db.CreateContext().ChatMessages.ToListAsync());
    }

    [Fact]
    public async Task Non_Participant_Gets_Not_Participant()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OutsiderId).ShareMeetingPointAsync(
            ConversationId, new ShareMeetingPointRequest { Kind = "home" });

        Assert.Equal("chat.not_participant", result.Error!.Code);
    }

    [Fact]
    public async Task Unknown_Conversation_Gets_Not_Found()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).ShareMeetingPointAsync(
            Guid.NewGuid(), new ShareMeetingPointRequest { Kind = "home" });

        Assert.Equal("chat.conversation_not_found", result.Error!.Code);
    }

    [Fact]
    public async Task Pending_Booking_Is_Unavailable()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, BookingStatus.Pending);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).ShareMeetingPointAsync(
            ConversationId, new ShareMeetingPointRequest { Kind = "home" });

        Assert.Equal("chat.meeting_point_unavailable", result.Error!.Code);
    }

    [Fact]
    public async Task Active_Booking_Allows_Sharing()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, BookingStatus.Active);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).ShareMeetingPointAsync(
            ConversationId, new ShareMeetingPointRequest { Kind = "home" });

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Completed_Booking_Is_Conversation_Closed()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db, BookingStatus.Completed);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).ShareMeetingPointAsync(
            ConversationId, new ShareMeetingPointRequest { Kind = "home" });

        Assert.Equal("chat.conversation_closed", result.Error!.Code);
    }

    [Fact]
    public async Task Explicitly_Closed_Conversation_Is_Conversation_Closed()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);
        await using (var ctx = db.CreateContext())
        {
            (await ctx.Conversations.SingleAsync()).ClosedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).ShareMeetingPointAsync(
            ConversationId, new ShareMeetingPointRequest { Kind = "home" });

        Assert.Equal("chat.conversation_closed", result.Error!.Code);
    }

    [Fact]
    public async Task Moderation_Thread_Is_Owner_Only_Refusal()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);
        Guid moderationId;
        await using (var ctx = db.CreateContext())
        {
            var store = new ConversationsStore(ctx, NullLogger<ConversationsStore>.Instance);
            moderationId = (await store.GetOrCreateForModerationAsync(OwnerId, RenterId)).Id;
        }

        await using var context = db.CreateContext();
        var result = await CreateService(context, RenterId).ShareMeetingPointAsync(
            moderationId, new ShareMeetingPointRequest { Kind = "home" });

        Assert.Equal("chat.meeting_point_owner_only", result.Error!.Code);
    }

    [Fact]
    public async Task Inbox_Shows_Location_Type_And_Null_Snippet_And_Details_Json_Carries_Fields()
    {
        using var db = new SqliteTestDatabase();
        await SeedAsync(db);

        await using (var ctx = db.CreateContext())
        {
            var shared = await CreateService(ctx, OwnerId).ShareMeetingPointAsync(
                ConversationId, new ShareMeetingPointRequest { Kind = "home" });
            Assert.True(shared.IsSuccess);
        }

        await using var context = db.CreateContext();
        var service = CreateService(context, RenterId);

        var inbox = await service.GetConversationsAsync();
        var item = Assert.Single(inbox.Value!);
        Assert.Equal("location", item.LastMessageType);
        Assert.Null(item.LastMessageSnippet);

        var details = await service.GetConversationAsync(ConversationId, null, null);
        var json = JsonSerializer.Serialize(
            Assert.Single(details.Value!.Messages),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("location", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("home", doc.RootElement.GetProperty("meetingPointKind").GetString());
        Assert.Equal(40.185612m, doc.RootElement.GetProperty("latitude").GetDecimal());
        Assert.Equal(44.512634m, doc.RootElement.GetProperty("longitude").GetDecimal());
    }
}
