using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Abstractions;

/// <summary>An inbox row: a conversation plus the current user's view of it.</summary>
/// <param name="LastMessageSenderId">
/// Sender of the conversation's last message. Null when there is no last message, or it was a
/// system message (system messages have a null sender).
/// </param>
public sealed record ChatConversationListItem(
    Conversation Conversation,
    User Counterpart,
    Booking Booking,
    int UnreadCount,
    Guid? LastMessageSenderId);

/// <summary>Full conversation view: the thread, its context, and a page of messages.</summary>
public sealed record ChatConversationDetails(
    Conversation Conversation,
    User Counterpart,
    Booking Booking,
    DateTime? CounterpartLastReadAt,
    IReadOnlyList<ChatMessage> Messages);

public interface IConversationsStore
{
    Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the conversation for a booking, creating it (and the two participant rows) on
    /// first access. Returns null when the booking does not exist or <paramref name="currentUserId"/>
    /// is neither its owner nor its renter.
    /// </summary>
    Task<Conversation?> GetOrCreateForBookingAsync(Guid bookingId, Guid currentUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// System-context get-or-create: returns the conversation for a booking, creating it (and the
    /// two participant rows) on first access, WITHOUT a caller-participant check — used by booking
    /// lifecycle events (see <see cref="IChatSystemMessageEmitter"/>), which are authoritative and
    /// have no acting "current user". Null only when the booking does not exist.
    /// </summary>
    Task<Conversation?> GetOrCreateForBookingSystemAsync(Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>Conversations where the user is owner or renter, newest activity first (nulls last).</summary>
    Task<IReadOnlyList<ChatConversationListItem>> ListForUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Conversation with a page of messages (newest page first). Null when it does not exist.</summary>
    Task<ChatConversationDetails?> GetDetailsAsync(
        Guid conversationId,
        Guid userId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>Loads a conversation without its messages (participant/closed checks). Null when absent.</summary>
    Task<Conversation?> FindByIdAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>Inserts a Text message and refreshes the conversation's denormalised preview fields.</summary>
    Task<ChatMessage> AddTextMessageAsync(Guid conversationId, Guid senderId, string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a System message (null sender) for the given kind and refreshes the conversation's
    /// denormalised preview fields, unless a System message of that same kind already exists in
    /// this conversation — a booking transition fires once, but the emit is made idempotent against
    /// retries. Returns null when skipped as a duplicate.
    /// </summary>
    Task<ChatMessage?> AddSystemMessageAsync(Guid conversationId, ChatSystemKind kind, string body, CancellationToken cancellationToken = default);

    /// <summary>Advances this participant's read cursor to the latest message. False when not a participant.</summary>
    Task<bool> MarkReadAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the booking's conversation (sets <c>ClosedAt</c>) for the read-only chat lock — per
    /// ADR-001, triggered once a booking is Completed AND both party reviews (owner review + renter
    /// review) are in. Idempotent: a no-op when the conversation does not exist or is already closed.
    /// Best-effort like <see cref="IChatSystemMessageEmitter"/>: implementations MUST NOT throw —
    /// failures are logged internally and swallowed so this can never break review submission or
    /// booking completion. Returns true only when this call is the one that closed it.
    /// </summary>
    Task<bool> CloseForBookingAsync(Guid bookingId, DateTime closedAtUtc, CancellationToken cancellationToken = default);
}
