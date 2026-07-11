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

    // Only one of the two gating reviews is in: the lazy close must NOT trigger, so messaging
    // keeps working and the pill still reads "completed" rather than "closed".
    [Fact]
    public async Task SendMessage_Succeeds_And_GetConversation_Stays_Completed_When_Only_One_Review_Exists()
    {
        using var db = new SqliteTestDatabase();
        await SeedCompletedBookingAsync(db, hasOwnerReview: true, hasRenterReview: false);

        await using var sendContext = db.CreateContext();
        var sendResult = await CreateService(sendContext, OwnerId).SendMessageAsync(new SendChatMessageRequest
        {
            ConversationId = ConversationId,
            Content = "Hello"
        });

        Assert.True(sendResult.IsSuccess);

        await using var verifyContext = db.CreateContext();
        var conversation = await verifyContext.Conversations.SingleAsync(c => c.Id == ConversationId);
        Assert.Null(conversation.ClosedAt);

        await using var readContext = db.CreateContext();
        var detailsResult = await CreateService(readContext, OwnerId)
            .GetConversationAsync(ConversationId, page: null, pageSize: null);

        Assert.True(detailsResult.IsSuccess);
        Assert.False(detailsResult.Value!.IsClosed);
        Assert.Equal("completed", detailsResult.Value!.Status);
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
