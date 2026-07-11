using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Tests.TestSupport;

// No-op (but recording) test double for realtime chat push. Delivery is best-effort (see
// IChatRealtimeNotifier), so most ChatService tests don't need it to do anything beyond not
// throwing — the recorded calls let a few tests assert the broadcast fired with the right
// payload/participants.
public sealed class FakeChatRealtimeNotifier : IChatRealtimeNotifier
{
    public List<(ChatRealtimeMessage Message, Guid OwnerId, Guid RenterId)> MessageSentCalls { get; } = new();

    public Task MessageSentAsync(
        ChatRealtimeMessage message,
        Guid ownerId,
        Guid renterId,
        CancellationToken cancellationToken = default)
    {
        MessageSentCalls.Add((message, ownerId, renterId));
        return Task.CompletedTask;
    }

    public Task ConversationReadAsync(
        Guid conversationId,
        Guid readerUserId,
        Guid otherUserId,
        DateTime readAtUtc,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
