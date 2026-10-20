using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Chat;

// Coverage for the two additive read-DTO fields: ChatConversationResponse.LastMessageIsMine
// (inbox preview) and ChatConversationDetailsResponse.CounterpartId (thread header). Each test
// runs the real ChatService over the real ConversationsStore/BookingsStore against an isolated
// in-memory SQLite database.
public sealed class ChatServiceTests
{
    private static readonly Guid OwnerId = new("b0000000-0000-0000-0000-000000000001");
    private static readonly Guid RenterId = new("b0000000-0000-0000-0000-000000000002");
    private static readonly Guid CategoryId = new("b0000000-0000-0000-0000-000000000003");
    private static readonly Guid ListingId = new("b0000000-0000-0000-0000-000000000004");
    private static readonly Guid BookingId = new("b0000000-0000-0000-0000-000000000005");
    private static readonly Guid ConversationId = new("b0000000-0000-0000-0000-000000000006");

    private static readonly DateOnly Today = TestData.Today;

    private static async Task SeedBaselineAsync(SqliteTestDatabase db)
    {
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@test.local"),
            TestData.User(RenterId, "renter@test.local"),
            TestData.Category(CategoryId));

        await db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId));
        await db.SeedAsync(TestData.Booking(
            BookingId, ListingId, RenterId,
            Today.AddDays(5), Today.AddDays(8),
            BookingStatus.Approved));
        await db.SeedAsync(TestData.Conversation(ConversationId, BookingId, OwnerId, RenterId));
    }

    private static ChatService CreateService(
        AppDbContext context,
        Guid currentUserId,
        FakeFileStorageService? fileStorageService = null,
        FakeChatRealtimeNotifier? realtimeNotifier = null) =>
        new(
            new FakeCurrentUserContext(currentUserId),
            new ConversationsStore(context, NullLogger<ConversationsStore>.Instance),
            new BookingsStore(context),
            new ReviewsStore(context),
            realtimeNotifier ?? new FakeChatRealtimeNotifier(),
            fileStorageService ?? new FakeFileStorageService());

    // Seeds a Completed booking + its conversation (ClosedAt still null, as if the M-010 close
    // event was missed by an older build) plus 0/1/2 of the party reviews that gate the ADR-001
    // lock, so tests can exercise ChatService's opportunistic TryLazyCloseAsync self-heal.
    private static async Task SeedCompletedBookingAsync(
        SqliteTestDatabase db, bool hasOwnerReview, bool hasRenterReview)
    {
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@test.local"),
            TestData.User(RenterId, "renter@test.local"),
            TestData.Category(CategoryId));

        await db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId));
        await db.SeedAsync(TestData.Booking(
            BookingId, ListingId, RenterId,
            Today.AddDays(-8), Today.AddDays(-5),
            BookingStatus.Completed));
        await db.SeedAsync(TestData.Conversation(ConversationId, BookingId, OwnerId, RenterId));

        if (hasOwnerReview)
        {
            await db.SeedAsync(new OwnerReview
            {
                Id = Guid.NewGuid(),
                BookingId = BookingId,
                OwnerId = OwnerId,
                ReviewerId = RenterId,
                CommunicationRating = 5,
                PickupHandoverRating = 5,
                FriendlinessRating = 5,
                CreatedAt = DateTime.UtcNow
            });
        }

        if (hasRenterReview)
        {
            await db.SeedAsync(new RenterReview
            {
                Id = Guid.NewGuid(),
                BookingId = BookingId,
                RenterId = RenterId,
                ReviewerId = OwnerId,
                CommunicationRating = 5,
                ReturnedOnTimeRating = 5,
                CareOfToyRating = 5,
                WouldRentAgainRating = 5,
                CreatedAt = DateTime.UtcNow
            });
        }
    }

    // Appends a message and refreshes the conversation's denormalised last-message pointer,
    // mirroring what ConversationsStore.AddTextMessageAsync/AddSystemMessageAsync do.
    private static async Task AddLastMessageAsync(SqliteTestDatabase db, Guid? senderId, MessageType type = MessageType.Text)
    {
        var messageId = Guid.NewGuid();
        await using var context = db.CreateContext();
        context.ChatMessages.Add(TestData.ChatMessage(messageId, ConversationId, senderId, type: type));
        var conversation = await context.Conversations.FindAsync(ConversationId);
        conversation!.LastMessageId = messageId;
        conversation.LastMessageAt = DateTime.UtcNow;
        conversation.LastMessageSnippet = "Hello";
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task List_LastMessageIsMine_True_When_CurrentUser_Sent_Last_Message()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await AddLastMessageAsync(db, senderId: OwnerId);

        await using var context = db.CreateContext();
        var service = CreateService(context, OwnerId);

        var result = await service.GetConversationsAsync();

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value!);
        Assert.True(item.LastMessageIsMine);
    }

    [Fact]
    public async Task List_LastMessageIsMine_False_When_Counterpart_Sent_Last_Message()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await AddLastMessageAsync(db, senderId: RenterId);

        await using var context = db.CreateContext();
        var service = CreateService(context, OwnerId);

        var result = await service.GetConversationsAsync();

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value!);
        Assert.False(item.LastMessageIsMine);
    }

    [Fact]
    public async Task List_LastMessageIsMine_False_For_System_Message()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await AddLastMessageAsync(db, senderId: null, type: MessageType.System);

        await using var context = db.CreateContext();
        var service = CreateService(context, OwnerId);

        var result = await service.GetConversationsAsync();

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value!);
        Assert.False(item.LastMessageIsMine);
    }

    [Fact]
    public async Task List_LastMessageIsMine_False_When_No_Last_Message()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        // No message added: Conversation.LastMessageId stays null.

        await using var context = db.CreateContext();
        var service = CreateService(context, OwnerId);

        var result = await service.GetConversationsAsync();

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value!);
        Assert.False(item.LastMessageIsMine);
    }

    // Inbox order is COALESCE(LastMessageAt, CreatedAt) DESC: a message-less conversation sorts by
    // its creation time, above threads whose last message is older and below those with a newer one.
    [Fact]
    public async Task List_Orders_By_Last_Activity_Using_CreatedAt_For_Messageless_Conversation()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@test.local"),
            TestData.User(RenterId, "renter@test.local"),
            TestData.Category(CategoryId));
        await db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId));

        var now = DateTime.UtcNow;
        var olderId = new Guid("b0000000-0000-0000-0000-0000000000a1");
        var newMessageless = new Guid("b0000000-0000-0000-0000-0000000000a2");
        var newerId = new Guid("b0000000-0000-0000-0000-0000000000a3");

        foreach (var (conversationId, createdAt, lastMessageAt) in new (Guid, DateTime, DateTime?)[]
        {
            (olderId, now.AddHours(-10), now.AddHours(-5)),       // last message older than the new thread's CreatedAt
            (newMessageless, now.AddHours(-2), null),             // brand new, no messages
            (newerId, now.AddHours(-10), now.AddHours(-1)),       // last message newer than the new thread's CreatedAt
        })
        {
            var bookingId = Guid.NewGuid();
            await db.SeedAsync(TestData.Booking(
                bookingId, ListingId, RenterId,
                Today.AddDays(5), Today.AddDays(8),
                BookingStatus.Approved));
            var conversation = TestData.Conversation(conversationId, bookingId, OwnerId, RenterId);
            conversation.CreatedAt = createdAt;
            conversation.LastMessageAt = lastMessageAt;
            await db.SeedAsync(conversation);
        }

        await using var context = db.CreateContext();
        var service = CreateService(context, OwnerId);

        var result = await service.GetConversationsAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(
            new[] { newerId, newMessageless, olderId },
            result.Value!.Select(item => item.Id).ToArray());
    }

    [Fact]
    public async Task Details_CounterpartId_Is_Renter_When_Viewer_Is_Owner()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var service = CreateService(context, OwnerId);

        var result = await service.GetConversationAsync(ConversationId, page: null, pageSize: null);

        Assert.True(result.IsSuccess);
        Assert.Equal(RenterId, result.Value!.CounterpartId);
    }

    [Fact]
    public async Task Details_CounterpartId_Is_Owner_When_Viewer_Is_Renter()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var service = CreateService(context, RenterId);

        var result = await service.GetConversationAsync(ConversationId, page: null, pageSize: null);

        Assert.True(result.IsSuccess);
        Assert.Equal(OwnerId, result.Value!.CounterpartId);
    }

    [Fact]
    public async Task SendMessage_Fails_When_Content_Exceeds_MaxContentLength()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var service = CreateService(context, OwnerId);

        var request = new SendChatMessageRequest
        {
            ConversationId = ConversationId,
            Content = new string('a', ChatService.MaxContentLength + 1)
        };

        var result = await service.SendMessageAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal("chat.message_too_long", result.Error!.Code);

        await using var verifyContext = db.CreateContext();
        Assert.Equal(0, await verifyContext.ChatMessages.CountAsync());
    }

    [Fact]
    public async Task SendMessage_Succeeds_When_Content_Is_Exactly_MaxContentLength()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var service = CreateService(context, OwnerId);

        var request = new SendChatMessageRequest
        {
            ConversationId = ConversationId,
            Content = new string('a', ChatService.MaxContentLength)
        };

        var result = await service.SendMessageAsync(request);

        Assert.True(result.IsSuccess);
    }

    // Opportunistic self-heal (M-010 family): the booking is Completed and both party reviews
    // are already in, but ClosedAt was never set (as if the close event fired on an older build).
    // SendMessageAsync must lazily close the conversation instead of letting the message through.
    [Fact]
    public async Task SendMessage_LazilyCloses_And_Fails_When_Booking_Completed_And_Both_Reviews_Exist()
    {
        using var db = new SqliteTestDatabase();
        await SeedCompletedBookingAsync(db, hasOwnerReview: true, hasRenterReview: true);

        await using var context = db.CreateContext();
        var service = CreateService(context, OwnerId);

        var request = new SendChatMessageRequest
        {
            ConversationId = ConversationId,
            Content = "Hello"
        };

        var result = await service.SendMessageAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal("chat.conversation_closed", result.Error!.Code);

        await using var verifyContext = db.CreateContext();
        var conversation = await verifyContext.Conversations.SingleAsync(c => c.Id == ConversationId);
        Assert.NotNull(conversation.ClosedAt);
        Assert.Equal(0, await verifyContext.ChatMessages.CountAsync());
    }

    [Fact]
    public async Task GetConversation_LazilyCloses_When_Booking_Completed_And_Both_Reviews_Exist()
    {
        using var db = new SqliteTestDatabase();
        await SeedCompletedBookingAsync(db, hasOwnerReview: true, hasRenterReview: true);

        await using var context = db.CreateContext();
        var service = CreateService(context, OwnerId);

        var result = await service.GetConversationAsync(ConversationId, page: null, pageSize: null);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsClosed);
        Assert.Equal("closed", result.Value!.Status);

        await using var verifyContext = db.CreateContext();
        var conversation = await verifyContext.Conversations.SingleAsync(c => c.Id == ConversationId);
        Assert.NotNull(conversation.ClosedAt);
    }

    // ADR-001 amended: a Completed booking is read-only even when only one (or no) review is in.
    // The legacy ClosedAt stays null (pill still "completed"), but sending is refused and IsClosed is derived.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task SendMessage_Fails_And_Details_IsClosed_When_Booking_Completed_Without_Both_Reviews(
        bool hasOwnerReview, bool hasRenterReview)
    {
        using var db = new SqliteTestDatabase();
        await SeedCompletedBookingAsync(db, hasOwnerReview, hasRenterReview);

        await using var sendContext = db.CreateContext();
        var sendResult = await CreateService(sendContext, OwnerId).SendMessageAsync(new SendChatMessageRequest
        {
            ConversationId = ConversationId,
            Content = "Hello"
        });

        Assert.False(sendResult.IsSuccess);
        Assert.Equal("chat.conversation_closed", sendResult.Error!.Code);

        await using var imageContext = db.CreateContext();
        var imageResult = await CreateService(imageContext, RenterId).SendImageMessageAsync(ImageRequest());
        Assert.False(imageResult.IsSuccess);
        Assert.Equal("chat.conversation_closed", imageResult.Error!.Code);

        await using var verifyContext = db.CreateContext();
        var conversation = await verifyContext.Conversations.SingleAsync(c => c.Id == ConversationId);
        Assert.Null(conversation.ClosedAt);
        Assert.Equal(0, await verifyContext.ChatMessages.CountAsync());

        await using var readContext = db.CreateContext();
        var detailsResult = await CreateService(readContext, OwnerId)
            .GetConversationAsync(ConversationId, page: null, pageSize: null);

        Assert.True(detailsResult.IsSuccess);
        Assert.True(detailsResult.Value!.IsClosed);
        Assert.Equal("completed", detailsResult.Value!.Status);
        Assert.Equal("none", detailsResult.Value.Turn);

        // MarkRead stays allowed on a read-only thread.
        await using var readMarkContext = db.CreateContext();
        Assert.True((await CreateService(readMarkContext, RenterId).MarkReadAsync(ConversationId)).IsSuccess);
    }

    // M-010: exercise the derived lock through the real Complete flow, not a seeded status.
    [Fact]
    public async Task SendMessage_Fails_After_Owner_Completes_Booking_Through_BookingsService()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@test.local"),
            TestData.User(RenterId, "renter@test.local"),
            TestData.Category(CategoryId));
        await db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId));
        await db.SeedAsync(TestData.Booking(
            BookingId, ListingId, RenterId,
            Today.AddDays(-3), Today.AddDays(-1),
            BookingStatus.Active));
        await db.SeedAsync(TestData.Conversation(ConversationId, BookingId, OwnerId, RenterId));

        await using (var bookingContext = db.CreateContext())
        {
            var bookings = new BookingsService(
                new FakeCurrentUserContext(OwnerId),
                new BookingsStore(bookingContext),
                new FakeNotificationEmitter(),
                new FakeChatSystemMessageEmitter(),
                new ReviewsStore(bookingContext),
                new ConversationsStore(bookingContext, NullLogger<ConversationsStore>.Instance));
            Assert.True((await bookings.CompleteAsync(BookingId)).IsSuccess);
        }

        await using var context = db.CreateContext();
        var result = await CreateService(context, RenterId).SendMessageAsync(new SendChatMessageRequest
        {
            ConversationId = ConversationId,
            Content = "Thanks!"
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("chat.conversation_closed", result.Error!.Code);
    }

    [Theory]
    [InlineData(true, "owner")]
    [InlineData(false, "renter")]
    public async Task Details_Exposes_Rental_State_For_Pending_Booking(bool viewerIsOwner, string expectedRole)
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@test.local"),
            TestData.User(RenterId, "renter@test.local"),
            TestData.Category(CategoryId));
        await db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId));
        var expiresAt = DateTime.UtcNow.AddHours(12);
        var booking = TestData.Booking(
            BookingId, ListingId, RenterId,
            Today.AddDays(5), Today.AddDays(8),
            BookingStatus.Pending, expiresAt: expiresAt);
        booking.RejectionReason = "stale";
        booking.Note = "Please keep it clean";
        await db.SeedAsync(booking);
        await db.SeedAsync(TestData.Conversation(ConversationId, BookingId, OwnerId, RenterId));

        await using var context = db.CreateContext();
        var result = await CreateService(context, viewerIsOwner ? OwnerId : RenterId)
            .GetConversationAsync(ConversationId, page: null, pageSize: null);

        Assert.True(result.IsSuccess);
        var details = result.Value!;
        Assert.Equal("requested", details.BookingStatus);
        Assert.Equal(expectedRole, details.ViewerRole);
        Assert.Equal("owner", details.Turn);
        Assert.Equal(expiresAt, details.DeadlineAt);
        Assert.Equal(Today.AddDays(5), details.StartDate);
        Assert.Equal(Today.AddDays(8), details.EndDate);
        Assert.Equal(3, details.RentalDays);
        Assert.Equal("stale", details.RejectionReason);
        Assert.Equal(ListingId, details.ListingId);
        Assert.Equal("Please keep it clean", details.RenterNote);
        Assert.False(details.IsClosed);
    }

    [Fact]
    public async Task Details_DeadlineAt_Is_Null_When_Booking_Not_Pending()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db); // Approved

        await using var context = db.CreateContext();
        var result = await CreateService(context, RenterId)
            .GetConversationAsync(ConversationId, page: null, pageSize: null);

        Assert.True(result.IsSuccess);
        Assert.Equal("approved", result.Value!.BookingStatus);
        Assert.Equal("owner", result.Value.Turn);
        Assert.Null(result.Value.DeadlineAt);
    }

    [Theory]
    [InlineData(BookingStatus.Rejected)]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Expired)]
    public async Task Details_Stays_Open_For_Rejected_Cancelled_Expired(BookingStatus status)
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@test.local"),
            TestData.User(RenterId, "renter@test.local"),
            TestData.Category(CategoryId));
        await db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId));
        await db.SeedAsync(TestData.Booking(
            BookingId, ListingId, RenterId, Today.AddDays(5), Today.AddDays(8), status));
        await db.SeedAsync(TestData.Conversation(ConversationId, BookingId, OwnerId, RenterId));

        await using var detailsContext = db.CreateContext();
        var details = await CreateService(detailsContext, OwnerId)
            .GetConversationAsync(ConversationId, page: null, pageSize: null);
        Assert.False(details.Value!.IsClosed);
        Assert.Equal("none", details.Value.Turn);

        await using var sendContext = db.CreateContext();
        var send = await CreateService(sendContext, OwnerId).SendMessageAsync(new SendChatMessageRequest
        {
            ConversationId = ConversationId,
            Content = "Still open"
        });
        Assert.True(send.IsSuccess);
    }

    [Theory]
    [InlineData(BookingStatus.Rejected)]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Expired)]
    public async Task Details_And_Inbox_Map_Rejected_Cancelled_Expired_To_NotHappened(BookingStatus status)
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@test.local"),
            TestData.User(RenterId, "renter@test.local"),
            TestData.Category(CategoryId));
        await db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId));
        await db.SeedAsync(TestData.Booking(
            BookingId, ListingId, RenterId, Today.AddDays(5), Today.AddDays(8), status));
        await db.SeedAsync(TestData.Conversation(ConversationId, BookingId, OwnerId, RenterId));

        await using var detailsContext = db.CreateContext();
        var details = await CreateService(detailsContext, OwnerId)
            .GetConversationAsync(ConversationId, page: null, pageSize: null);
        Assert.Equal("not_happened", details.Value!.Status);
        Assert.Equal("not_happened", details.Value.BookingStatus);

        await using var listContext = db.CreateContext();
        var list = await CreateService(listContext, OwnerId).GetConversationsAsync();
        Assert.Equal("not_happened", Assert.Single(list.Value!).Status);
    }

    [Fact]
    public async Task Details_Rental_Fields_Are_Null_For_Moderation_Thread()
    {
        var adminId = Guid.NewGuid();
        var moderationId = Guid.NewGuid();
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(
            TestData.User(adminId, "admin@test.local"),
            TestData.User(RenterId, "member@test.local"));
        await db.SeedAsync(new Conversation
        {
            Id = moderationId,
            BookingId = null,
            Kind = ConversationKind.Moderation,
            OwnerId = adminId,
            RenterId = RenterId,
            CreatedAt = DateTime.UtcNow
        });

        await using var context = db.CreateContext();
        var result = await CreateService(context, RenterId)
            .GetConversationAsync(moderationId, page: null, pageSize: null);

        Assert.True(result.IsSuccess);
        var details = result.Value!;
        Assert.Null(details.BookingStatus);
        Assert.Null(details.ViewerRole);
        Assert.Null(details.Turn);
        Assert.Null(details.DeadlineAt);
        Assert.Null(details.StartDate);
        Assert.Null(details.EndDate);
        Assert.Null(details.RentalDays);
        Assert.Null(details.RejectionReason);
        Assert.Null(details.ListingId);
        Assert.Null(details.RenterNote);
        Assert.False(details.IsClosed);
    }

    private static SendChatImageMessageRequest ImageRequest(
        byte[]? content = null,
        string fileName = "photo.png",
        string contentType = "image/png",
        long? declaredLength = null,
        string? caption = null) => new()
    {
        ConversationId = ConversationId,
        FileName = fileName,
        ContentType = contentType,
        Length = declaredLength ?? (content ?? TestData.PngBytes()).Length,
        Content = new MemoryStream(content ?? TestData.PngBytes()),
        Caption = caption
    };

    [Fact]
    public async Task SendImageMessage_Succeeds_Persists_Image_Updates_Preview_And_Broadcasts()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        var fileStorage = new FakeFileStorageService();
        var realtimeNotifier = new FakeChatRealtimeNotifier();

        await using var context = db.CreateContext();
        var service = CreateService(context, OwnerId, fileStorage, realtimeNotifier);

        var result = await service.SendImageMessageAsync(ImageRequest(caption: "Check this out"));

        Assert.True(result.IsSuccess);
        Assert.Equal("image", result.Value!.Type);
        Assert.NotNull(result.Value.AttachmentUrl);
        Assert.Equal("Check this out", result.Value.Body);

        var savedUrl = Assert.Single(fileStorage.SavedChatUrls);
        Assert.Equal(savedUrl, result.Value.AttachmentUrl);

        await using var verifyContext = db.CreateContext();
        var message = await verifyContext.ChatMessages.SingleAsync();
        Assert.Equal(MessageType.Image, message.Type);
        Assert.Equal(savedUrl, message.AttachmentUrl);
        Assert.Equal("Check this out", message.Body);

        var conversation = await verifyContext.Conversations.SingleAsync(c => c.Id == ConversationId);
        Assert.Equal(message.Id, conversation.LastMessageId);
        Assert.NotNull(conversation.LastMessageAt);
        Assert.Equal("Check this out", conversation.LastMessageSnippet);

        var call = Assert.Single(realtimeNotifier.MessageSentCalls);
        Assert.Equal(savedUrl, call.Message.AttachmentUrl);
        Assert.Equal("image", call.Message.Type);
        Assert.Equal(OwnerId, call.OwnerId);
        Assert.Equal(RenterId, call.RenterId);
    }

    [Fact]
    public async Task SendImageMessage_Preview_Snippet_Is_Null_When_No_Caption()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var service = CreateService(context, OwnerId);

        var result = await service.SendImageMessageAsync(ImageRequest());

        Assert.True(result.IsSuccess);

        await using var verifyContext = db.CreateContext();
        var conversation = await verifyContext.Conversations.SingleAsync(c => c.Id == ConversationId);
        Assert.Null(conversation.LastMessageSnippet);
    }

    [Fact]
    public async Task SendImageMessage_Fails_When_Conversation_Closed()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        await using (var closeContext = db.CreateContext())
        {
            var conversation = await closeContext.Conversations.SingleAsync(c => c.Id == ConversationId);
            conversation.ClosedAt = DateTime.UtcNow;
            await closeContext.SaveChangesAsync();
        }

        await using var context = db.CreateContext();
        var service = CreateService(context, OwnerId);

        var result = await service.SendImageMessageAsync(ImageRequest());

        Assert.False(result.IsSuccess);
        Assert.Equal("chat.conversation_closed", result.Error!.Code);

        await using var verifyContext = db.CreateContext();
        Assert.Equal(0, await verifyContext.ChatMessages.CountAsync());
    }

    [Fact]
    public async Task SendImageMessage_Fails_When_Not_Participant()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        var strangerId = Guid.NewGuid();
        await db.SeedAsync(TestData.User(strangerId, "stranger@test.local"));

        await using var context = db.CreateContext();
        var service = CreateService(context, strangerId);

        var result = await service.SendImageMessageAsync(ImageRequest());

        Assert.False(result.IsSuccess);
        Assert.Equal("chat.not_participant", result.Error!.Code);

        await using var verifyContext = db.CreateContext();
        Assert.Equal(0, await verifyContext.ChatMessages.CountAsync());
    }

    [Fact]
    public async Task SendImageMessage_Fails_When_File_Exceeds_Size_Limit()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var service = CreateService(context, OwnerId);

        var oversized = TestData.PngBytes((int)ChatService.MaxAttachmentBytes + 1);
        var result = await service.SendImageMessageAsync(ImageRequest(content: oversized));

        Assert.False(result.IsSuccess);
        Assert.Equal("chat.attachment_too_large", result.Error!.Code);

        await using var verifyContext = db.CreateContext();
        Assert.Equal(0, await verifyContext.ChatMessages.CountAsync());
    }

    // Bytes served under an image Content-Type whose magic bytes disagree with the claim —
    // the second validation layer (ImageContentValidator) must catch what the header lies about.
    [Fact]
    public async Task SendImageMessage_Fails_When_Magic_Bytes_Disagree_With_Claimed_Content_Type()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var service = CreateService(context, OwnerId);

        var result = await service.SendImageMessageAsync(ImageRequest(content: TestData.NonImageBytes()));

        Assert.False(result.IsSuccess);
        Assert.Equal("chat.attachment_invalid_type", result.Error!.Code);

        await using var verifyContext = db.CreateContext();
        Assert.Equal(0, await verifyContext.ChatMessages.CountAsync());
    }

    [Fact]
    public async Task List_LastMessageType_Is_Text_Token_For_Text_Last_Message()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await AddLastMessageAsync(db, senderId: OwnerId, type: MessageType.Text);

        await using var context = db.CreateContext();
        var service = CreateService(context, OwnerId);

        var result = await service.GetConversationsAsync();

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value!);
        Assert.Equal("text", item.LastMessageType);
    }

    [Fact]
    public async Task List_LastMessageType_Is_Image_Token_For_Image_Last_Message()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await AddLastMessageAsync(db, senderId: OwnerId, type: MessageType.Image);

        await using var context = db.CreateContext();
        var service = CreateService(context, OwnerId);

        var result = await service.GetConversationsAsync();

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value!);
        Assert.Equal("image", item.LastMessageType);
    }

    [Fact]
    public async Task List_LastMessageSystemKind_Is_Token_When_Last_Message_Is_System_Line()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        var messageId = Guid.NewGuid();
        await using (var seed = db.CreateContext())
        {
            var message = TestData.ChatMessage(messageId, ConversationId, null, type: MessageType.System);
            message.SystemKind = ChatSystemKind.Rejected;
            seed.ChatMessages.Add(message);
            var conversation = await seed.Conversations.FindAsync(ConversationId);
            conversation!.LastMessageId = messageId;
            conversation.LastMessageAt = DateTime.UtcNow;
            conversation.LastMessageSnippet = "The owner declined the request.";
            await seed.SaveChangesAsync();
        }

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).GetConversationsAsync();

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value!);
        Assert.Equal("system", item.LastMessageType);
        Assert.Equal("rejected", item.LastMessageSystemKind);
    }

    [Fact]
    public async Task List_LastMessageSystemKind_Is_Null_For_Text_Last_Message()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        await AddLastMessageAsync(db, senderId: OwnerId, type: MessageType.Text);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).GetConversationsAsync();

        var item = Assert.Single(result.Value!);
        Assert.Null(item.LastMessageSystemKind);
    }

    [Fact]
    public async Task List_LastMessageSystemKind_Is_Null_When_No_Last_Message()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);

        await using var context = db.CreateContext();
        var result = await CreateService(context, OwnerId).GetConversationsAsync();

        var item = Assert.Single(result.Value!);
        Assert.Null(item.LastMessageSystemKind);
    }

    [Fact]
    public async Task List_LastMessageType_Is_Null_When_No_Last_Message()
    {
        using var db = new SqliteTestDatabase();
        await SeedBaselineAsync(db);
        // No message added: Conversation.LastMessageId stays null.

        await using var context = db.CreateContext();
        var service = CreateService(context, OwnerId);

        var result = await service.GetConversationsAsync();

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value!);
        Assert.Null(item.LastMessageType);
    }
}
