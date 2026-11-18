using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Push;

// ADR-067 section 10: the push is requested right beside the realtime notification, by the services that save
// the message. The system-line emitter has no push dependency, so a system line can never reach the queue.
public sealed class ChatPushWiringTests : IDisposable
{
    private static readonly Guid OwnerId = new("e3000000-0000-0000-0000-000000000001");
    private static readonly Guid RenterId = new("e3000000-0000-0000-0000-000000000002");
    private static readonly Guid CategoryId = new("e3000000-0000-0000-0000-000000000003");
    private static readonly Guid ListingId = new("e3000000-0000-0000-0000-000000000004");
    private static readonly Guid BookingId = new("e3000000-0000-0000-0000-000000000005");
    private static readonly Guid ConversationId = new("e3000000-0000-0000-0000-000000000006");

    private readonly SqliteTestDatabase _db = new();
    private readonly FakeChatPushNotifier _push = new();

    public void Dispose() => _db.Dispose();

    private async Task SeedAsync()
    {
        await _db.SeedAsync(
            TestData.User(OwnerId, "owner@wiring.local"),
            TestData.User(RenterId, "renter@wiring.local"),
            TestData.Category(CategoryId));
        await _db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId));
        await _db.SeedAsync(TestData.Booking(
            BookingId, ListingId, RenterId, TestData.Today.AddDays(3), TestData.Today.AddDays(5), BookingStatus.Approved));
        await _db.SeedAsync(TestData.Conversation(ConversationId, BookingId, OwnerId, RenterId));
    }

    [Fact]
    public async Task A_Text_Message_Requests_A_Push()
    {
        await SeedAsync();
        await using var context = _db.CreateContext();
        var service = new ChatService(
            new FakeCurrentUserContext(RenterId),
            new ConversationsStore(context, NullLogger<ConversationsStore>.Instance),
            new BookingsStore(context),
            new ReviewsStore(context),
            new FakeChatRealtimeNotifier(),
            _push,
            new FakeFileStorageService(),
            new DistrictBoundaryProvider());

        var result = await service.SendMessageAsync(new SendChatMessageRequest { ConversationId = ConversationId, Content = "hi" });

        Assert.True(result.IsSuccess);
        var call = Assert.Single(_push.Calls);
        Assert.Equal(ConversationId, call.Message.ConversationId);
        Assert.Equal(RenterId, call.Message.SenderId);
        Assert.Equal(OwnerId, call.OwnerId);
        Assert.Equal(RenterId, call.RenterId);
    }

    [Fact]
    public async Task A_Moderator_Note_Requests_A_Push()
    {
        var adminId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        await _db.SeedAsync(
            TestData.User(adminId, "admin@wiring.local", role: UserRole.Admin),
            TestData.User(memberId, "member@wiring.local"));
        await using var context = _db.CreateContext();
        var emitter = new ModerationNoteEmitter(
            new ConversationsStore(context, NullLogger<ConversationsStore>.Instance),
            new FakeChatRealtimeNotifier(),
            _push,
            NullLogger<ModerationNoteEmitter>.Instance);

        await emitter.AccountSuspendedAsync(adminId, memberId, "en", "Spam");

        var call = Assert.Single(_push.Calls);
        Assert.Equal("moderationNote", call.Message.Type);
        Assert.Equal(adminId, call.Message.SenderId);
        Assert.Equal(memberId, call.RenterId);
    }
}
