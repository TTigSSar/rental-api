using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Tests.TestSupport;

// No-op test double for realtime chat push. Delivery is best-effort (see IChatRealtimeNotifier),
// so tests of ChatService's core read/write behavior don't need it to do anything.
public sealed class FakeChatRealtimeNotifier : IChatRealtimeNotifier
{
    public Task MessageSentAsync(
        ChatRealtimeMessage message,
        Guid ownerId,
        Guid renterId,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ConversationReadAsync(
        Guid conversationId,
        Guid readerUserId,
        Guid otherUserId,
        DateTime readAtUtc,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
