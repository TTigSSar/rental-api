using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// ModerationNoteEmitter mirrors ChatSystemMessageEmitter's try/catch-swallow + log doctrine: a
// note failure must never propagate to (and so never fail) the moderation action that triggered
// it. Each of the three emit methods is exercised against a store where every member throws.
public sealed class ModerationNoteEmitterTests
{
    private static ModerationNoteEmitter CreateEmitter() =>
        new(
            new ThrowingConversationsStore(),
            new FakeChatRealtimeNotifier(),
            NullLogger<ModerationNoteEmitter>.Instance);

    [Fact]
    public async Task ListingRejectedAsync_Swallows_Store_Failure()
    {
        var emitter = CreateEmitter();

        var exception = await Record.ExceptionAsync(() =>
            emitter.ListingRejectedAsync(Guid.NewGuid(), Guid.NewGuid(), "Some Listing", "Poor or missing images", "Please add photos."));

        Assert.Null(exception);
    }

    [Fact]
    public async Task ListingRecategorisedAsync_Swallows_Store_Failure()
    {
        var emitter = CreateEmitter();

        var exception = await Record.ExceptionAsync(() =>
            emitter.ListingRecategorisedAsync(Guid.NewGuid(), Guid.NewGuid(), "Some Listing", "Building Blocks", "Outdoor Toys"));

        Assert.Null(exception);
    }

    [Fact]
    public async Task AccountSuspendedAsync_Swallows_Store_Failure()
    {
        var emitter = CreateEmitter();

        var exception = await Record.ExceptionAsync(() =>
            emitter.AccountSuspendedAsync(Guid.NewGuid(), Guid.NewGuid(), "Repeated policy violations."));

        Assert.Null(exception);
    }
}
