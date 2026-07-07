using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

/// <summary>
/// Pushes chat events to connected clients over realtime transport (SignalR, in Api).
/// Implementations MUST NOT throw — like <see cref="IEmailService"/>, realtime delivery
/// is best-effort and a failure here must never roll back or fail the message send / mark-read
/// operation that triggered it.
/// </summary>
public interface IChatRealtimeNotifier
{
    /// <summary>A message was sent in a conversation → push it live to both participants.</summary>
    Task MessageSentAsync(
        ChatRealtimeMessage message,
        Guid ownerId,
        Guid renterId,
        CancellationToken cancellationToken = default);

    /// <summary>A participant marked a conversation read → push the read event to the other participant.</summary>
    Task ConversationReadAsync(
        Guid conversationId,
        Guid readerUserId,
        Guid otherUserId,
        DateTime readAtUtc,
        CancellationToken cancellationToken = default);
}
