using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

/// <summary>
/// Queues a Web Push for a chat message that was just saved (ADR-067). Called right beside
/// <see cref="IChatRealtimeNotifier.MessageSentAsync"/>, for human lines and moderator notes only, never for
/// system lines. Implementations MUST NOT throw and must not block: push is best-effort and must never roll
/// back or fail the send that triggered it.
/// </summary>
public interface IChatPushNotifier
{
    Task MessageSentAsync(
        ChatRealtimeMessage message,
        Guid ownerId,
        Guid renterId,
        CancellationToken cancellationToken = default);
}
