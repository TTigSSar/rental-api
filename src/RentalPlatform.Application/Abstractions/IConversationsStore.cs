using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Abstractions;

/// <summary>An inbox row: a conversation plus the current user's view of it.</summary>
public sealed record ChatConversationListItem(
    Conversation Conversation,
    User Counterpart,
    Booking Booking,
    int UnreadCount);

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

    /// <summary>Advances this participant's read cursor to the latest message. False when not a participant.</summary>
    Task<bool> MarkReadAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken = default);
}
