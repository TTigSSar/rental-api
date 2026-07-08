using RentalPlatform.Application.Services;
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

    private static ChatService CreateService(AppDbContext context, Guid currentUserId) =>
        new(
            new FakeCurrentUserContext(currentUserId),
            new ConversationsStore(context),
            new BookingsStore(context),
            new FakeChatRealtimeNotifier());

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
}
