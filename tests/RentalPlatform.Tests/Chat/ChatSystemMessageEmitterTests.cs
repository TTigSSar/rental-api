using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Chat;

// Runs the REAL ChatSystemMessageEmitter + ConversationsStore against SQLite (M-013).
// ADR-051: the stored body is a fixed English fallback and never carries user data.
public sealed class ChatSystemMessageEmitterTests
{
    private static readonly Guid OwnerId = new("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid RenterId = new("c0000000-0000-0000-0000-000000000002");
    private static readonly Guid CategoryId = new("c0000000-0000-0000-0000-000000000003");
    private static readonly Guid ListingId = new("c0000000-0000-0000-0000-000000000004");
    private static readonly Guid BookingId = new("c0000000-0000-0000-0000-000000000005");

    private const string FixedBody = "The owner declined the request.";

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("Dates do not work for me, call 077123456")]
    public async Task BookingRejected_Stores_Fixed_Body_Regardless_Of_RejectionReason(string? reason)
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(
            TestData.User(OwnerId, "owner@test.local"),
            TestData.User(RenterId, "renter@test.local"),
            TestData.Category(CategoryId));
        await db.SeedAsync(TestData.Listing(ListingId, OwnerId, CategoryId));
        var booking = TestData.Booking(
            BookingId, ListingId, RenterId,
            TestData.Today.AddDays(5), TestData.Today.AddDays(8),
            BookingStatus.Rejected);
        booking.RejectionReason = reason;
        await db.SeedAsync(booking);

        var notifier = new FakeChatRealtimeNotifier();
        await using var context = db.CreateContext();
        var emitter = new ChatSystemMessageEmitter(
            new ConversationsStore(context, NullLogger<ConversationsStore>.Instance),
            notifier,
            NullLogger<ChatSystemMessageEmitter>.Instance);

        await emitter.BookingRejectedAsync(booking, OwnerId);

        await using var verify = db.CreateContext();
        var message = Assert.Single(verify.ChatMessages);
        Assert.Equal(MessageType.System, message.Type);
        Assert.Equal(ChatSystemKind.Rejected, message.SystemKind);
        Assert.Equal(FixedBody, message.Body);

        var broadcast = Assert.Single(notifier.MessageSentCalls).Message;
        Assert.Equal(FixedBody, broadcast.Body);
        Assert.Equal("rejected", broadcast.SystemKind);
    }
}
