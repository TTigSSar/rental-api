using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Services;

public sealed class ChatService : IChatService
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 100;

    private static class ErrorCodes
    {
        public const string Unauthenticated = "chat.unauthenticated";
        public const string UserBlocked = "chat.user_blocked";
        public const string NotParticipant = "chat.not_participant";
        public const string ConversationNotFound = "chat.conversation_not_found";
        public const string BookingNotFound = "chat.booking_not_found";
        public const string ConversationClosed = "chat.conversation_closed";
    }

    private readonly ICurrentUserContext _currentUserContext;
    private readonly IConversationsStore _store;
    private readonly IBookingsStore _bookingsStore;
    private readonly IChatRealtimeNotifier _realtimeNotifier;

    public ChatService(
        ICurrentUserContext currentUserContext,
        IConversationsStore store,
        IBookingsStore bookingsStore,
        IChatRealtimeNotifier realtimeNotifier)
    {
        _currentUserContext = currentUserContext;
        _store = store;
        _bookingsStore = bookingsStore;
        _realtimeNotifier = realtimeNotifier;
    }

    public async Task<ServiceResult<IReadOnlyCollection<ChatConversationResponse>>> GetConversationsAsync(
        CancellationToken cancellationToken = default)
    {
        if (_currentUserContext.UserId is not { } userId)
        {
            return Failure<IReadOnlyCollection<ChatConversationResponse>>(ErrorCodes.Unauthenticated, "Current user is not authenticated.");
        }

        var now = DateTime.UtcNow;
        var items = await _store.ListForUserAsync(userId, cancellationToken);
        var response = items.Select(item => MapListItem(item, userId, now)).ToList();

        return ServiceResult<IReadOnlyCollection<ChatConversationResponse>>.Success(response);
    }

    public async Task<ServiceResult<ChatConversationDetailsResponse>> GetConversationAsync(
        Guid conversationId,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken = default)
    {
        if (_currentUserContext.UserId is not { } userId)
        {
            return Failure<ChatConversationDetailsResponse>(ErrorCodes.Unauthenticated, "Current user is not authenticated.");
        }

        var normalizedPage = page is > 0 ? page.Value : 1;
        var normalizedPageSize = pageSize is > 0 ? Math.Min(pageSize.Value, MaxPageSize) : DefaultPageSize;

        var details = await _store.GetDetailsAsync(conversationId, userId, normalizedPage, normalizedPageSize, cancellationToken);
        if (details is null)
        {
            return Failure<ChatConversationDetailsResponse>(ErrorCodes.ConversationNotFound, "Conversation was not found.");
        }

        if (!IsParticipant(details.Conversation, userId))
        {
            return Failure<ChatConversationDetailsResponse>(ErrorCodes.NotParticipant, "You are not a participant of this conversation.");
        }

        return ServiceResult<ChatConversationDetailsResponse>.Success(MapDetails(details, userId, DateTime.UtcNow));
    }

    public async Task<ServiceResult<ChatConversationDetailsResponse>> GetOrCreateForBookingAsync(
        Guid bookingId,
        CancellationToken cancellationToken = default)
    {
        if (_currentUserContext.UserId is not { } userId)
        {
            return Failure<ChatConversationDetailsResponse>(ErrorCodes.Unauthenticated, "Current user is not authenticated.");
        }

        // Resolve the booking first so we can return distinct booking-not-found vs not-participant
        // outcomes (the store's get-or-create collapses both to a null result).
        var booking = await _bookingsStore.FindBookingWithRelationsByIdAsync(bookingId, cancellationToken);
        if (booking is null)
        {
            return Failure<ChatConversationDetailsResponse>(ErrorCodes.BookingNotFound, "Booking was not found.");
        }

        if (userId != booking.Listing.OwnerId && userId != booking.RenterId)
        {
            return Failure<ChatConversationDetailsResponse>(ErrorCodes.NotParticipant, "You are not a participant of this booking.");
        }

        var conversation = await _store.GetOrCreateForBookingAsync(bookingId, userId, cancellationToken);
        if (conversation is null)
        {
            // Defensive: participant already verified above, so this only trips on a concurrent delete.
            return Failure<ChatConversationDetailsResponse>(ErrorCodes.BookingNotFound, "Booking was not found.");
        }

        var details = await _store.GetDetailsAsync(conversation.Id, userId, 1, DefaultPageSize, cancellationToken);
        if (details is null)
        {
            return Failure<ChatConversationDetailsResponse>(ErrorCodes.ConversationNotFound, "Conversation was not found.");
        }

        return ServiceResult<ChatConversationDetailsResponse>.Success(MapDetails(details, userId, DateTime.UtcNow));
    }

    public async Task<ServiceResult<ChatMessageResponse>> SendMessageAsync(
        SendChatMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_currentUserContext.UserId is not { } userId)
        {
            return Failure<ChatMessageResponse>(ErrorCodes.Unauthenticated, "Current user is not authenticated.");
        }

        var user = await _store.FindUserByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return Failure<ChatMessageResponse>(ErrorCodes.Unauthenticated, "Current user is not authenticated.");
        }

        if (user.IsBlocked)
        {
            return Failure<ChatMessageResponse>(ErrorCodes.UserBlocked, "Blocked users cannot send messages.");
        }

        var conversation = await _store.FindByIdAsync(request.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return Failure<ChatMessageResponse>(ErrorCodes.ConversationNotFound, "Conversation was not found.");
        }

        if (!IsParticipant(conversation, userId))
        {
            return Failure<ChatMessageResponse>(ErrorCodes.NotParticipant, "You are not a participant of this conversation.");
        }

        if (conversation.ClosedAt is not null)
        {
            return Failure<ChatMessageResponse>(ErrorCodes.ConversationClosed, "This conversation is closed.");
        }

        var message = await _store.AddTextMessageAsync(request.ConversationId, userId, request.Content, cancellationToken);

        // A brand-new message cannot yet have been read by the counterpart, so Seen is false.
        var response = MapMessage(message, userId, user, counterpartLastReadAt: null);

        var realtimeMessage = MapRealtimeMessage(message, DisplayName(user));
        await _realtimeNotifier.MessageSentAsync(realtimeMessage, conversation.OwnerId, conversation.RenterId, cancellationToken);

        return ServiceResult<ChatMessageResponse>.Success(response);
    }

    public async Task<ServiceResult<bool>> MarkReadAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        if (_currentUserContext.UserId is not { } userId)
        {
            return Failure<bool>(ErrorCodes.Unauthenticated, "Current user is not authenticated.");
        }

        var conversation = await _store.FindByIdAsync(conversationId, cancellationToken);
        if (conversation is null)
        {
            return Failure<bool>(ErrorCodes.ConversationNotFound, "Conversation was not found.");
        }

        if (!IsParticipant(conversation, userId))
        {
            return Failure<bool>(ErrorCodes.NotParticipant, "You are not a participant of this conversation.");
        }

        await _store.MarkReadAsync(conversationId, userId, cancellationToken);

        var otherUserId = conversation.OwnerId == userId ? conversation.RenterId : conversation.OwnerId;
        await _realtimeNotifier.ConversationReadAsync(conversationId, userId, otherUserId, DateTime.UtcNow, cancellationToken);

        return ServiceResult<bool>.Success(true);
    }

    private static bool IsParticipant(Conversation conversation, Guid userId) =>
        conversation.OwnerId == userId || conversation.RenterId == userId;

    private static ChatConversationResponse MapListItem(ChatConversationListItem item, Guid userId, DateTime utcNow) => new()
    {
        Id = item.Conversation.Id,
        BookingId = item.Conversation.BookingId,
        CounterpartName = DisplayName(item.Counterpart),
        CounterpartAvatarUrl = item.Counterpart.AvatarUrl,
        ToyTitle = item.Conversation.ToyTitle,
        ToyImageUrl = item.Conversation.ToyImageUrl,
        Status = ChatTokens.StatusToken(item.Booking.Status, item.Booking.EndDate, item.Conversation.ClosedAt, utcNow),
        LastMessageSnippet = item.Conversation.LastMessageSnippet,
        LastMessageAt = item.Conversation.LastMessageAt,
        LastMessageIsMine = item.LastMessageSenderId == userId,
        UnreadCount = item.UnreadCount
    };

    private static ChatConversationDetailsResponse MapDetails(ChatConversationDetails details, Guid userId, DateTime utcNow) => new()
    {
        Id = details.Conversation.Id,
        BookingId = details.Conversation.BookingId,
        CounterpartId = details.Conversation.OwnerId == userId ? details.Conversation.RenterId : details.Conversation.OwnerId,
        CounterpartName = DisplayName(details.Counterpart),
        CounterpartAvatarUrl = details.Counterpart.AvatarUrl,
        CounterpartVerified = details.Counterpart.IsEmailConfirmed && details.Counterpart.IsPhoneConfirmed,
        ToyTitle = details.Conversation.ToyTitle,
        ToyImageUrl = details.Conversation.ToyImageUrl,
        Status = ChatTokens.StatusToken(details.Booking.Status, details.Booking.EndDate, details.Conversation.ClosedAt, utcNow),
        BookingDates = FormatDates(details.Booking.StartDate, details.Booking.EndDate),
        BookingPrice = details.Booking.TotalPrice,
        IsClosed = details.Conversation.ClosedAt is not null,
        Messages = details.Messages
            .Select(message => MapMessage(message, userId, details.Counterpart, details.CounterpartLastReadAt))
            .ToList()
    };

    // The counterpart argument is the "other" user relative to userId; used only to resolve
    // the sender's display name when the message is the counterpart's.
    private static ChatMessageResponse MapMessage(
        ChatMessage message,
        Guid userId,
        User? counterpart,
        DateTime? counterpartLastReadAt)
    {
        var isMine = message.SenderId == userId;
        string? senderName = message.SenderId is null
            ? null
            : isMine
                ? null
                : counterpart is not null && counterpart.Id == message.SenderId
                    ? DisplayName(counterpart)
                    : null;

        var seen = isMine
            && counterpartLastReadAt is { } lastRead
            && lastRead >= message.CreatedAt;

        return new ChatMessageResponse
        {
            Id = message.Id,
            ConversationId = message.ConversationId,
            SenderId = message.SenderId,
            SenderName = senderName,
            Type = ChatTokens.MessageTypeToken(message.Type),
            SystemKind = ChatTokens.SystemKindToken(message.SystemKind),
            Body = message.Body,
            AttachmentUrl = message.AttachmentUrl,
            SentAt = message.CreatedAt,
            IsMine = isMine,
            Seen = seen
        };
    }

    // Viewer-neutral broadcast payload: unlike MapMessage, always carries the real sender
    // name (both recipients need it — neither is "you" from the broadcast's point of view).
    private static ChatRealtimeMessage MapRealtimeMessage(ChatMessage message, string? senderName) => new()
    {
        Id = message.Id,
        ConversationId = message.ConversationId,
        SenderId = message.SenderId,
        SenderName = message.SenderId is null ? null : senderName,
        Type = ChatTokens.MessageTypeToken(message.Type),
        SystemKind = ChatTokens.SystemKindToken(message.SystemKind),
        Body = message.Body,
        AttachmentUrl = message.AttachmentUrl,
        SentAt = message.CreatedAt
    };

    private static string DisplayName(User user) => $"{user.FirstName} {user.LastName}".Trim();

    private static string FormatDates(DateOnly startDate, DateOnly endDate) =>
        $"{startDate:yyyy-MM-dd} – {endDate:yyyy-MM-dd}";

    private static ServiceResult<T> Failure<T>(string code, string message) =>
        ServiceResult<T>.Failure(new ServiceError { Code = code, Message = message });
}
