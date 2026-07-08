using Microsoft.Extensions.Logging;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Infrastructure.Services;

/// <summary>
/// Inserts a System <see cref="ChatMessage"/> into a booking's conversation on each lifecycle
/// transition and broadcasts it live. Every method is best-effort: failures are logged and
/// swallowed so a chat problem can never break the booking action that triggered it — mirrors
/// <see cref="NotificationEmitter"/>.
/// </summary>
public sealed class ChatSystemMessageEmitter : IChatSystemMessageEmitter
{
    private const string RequestedBody = "Booking requested.";
    private const string ApprovedBody = "The owner approved the request.";
    private const string HandedOverBody = "Toy handed over — the rental has started.";
    private const string CompletedBody = "The rental is complete.";

    private readonly IConversationsStore _store;
    private readonly IChatRealtimeNotifier _realtimeNotifier;
    private readonly ILogger<ChatSystemMessageEmitter> _logger;

    public ChatSystemMessageEmitter(
        IConversationsStore store,
        IChatRealtimeNotifier realtimeNotifier,
        ILogger<ChatSystemMessageEmitter> logger)
    {
        _store = store;
        _realtimeNotifier = realtimeNotifier;
        _logger = logger;
    }

    public Task BookingRequestedAsync(Booking booking, CancellationToken cancellationToken = default) =>
        EmitAsync(booking, ChatSystemKind.Request, RequestedBody, cancellationToken);

    public Task BookingApprovedAsync(Booking booking, CancellationToken cancellationToken = default) =>
        EmitAsync(booking, ChatSystemKind.Approved, ApprovedBody, cancellationToken);

    public Task BookingHandedOverAsync(Booking booking, CancellationToken cancellationToken = default) =>
        EmitAsync(booking, ChatSystemKind.Handover, HandedOverBody, cancellationToken);

    public Task BookingCompletedAsync(Booking booking, CancellationToken cancellationToken = default) =>
        EmitAsync(booking, ChatSystemKind.Closed, CompletedBody, cancellationToken);

    private async Task EmitAsync(Booking booking, ChatSystemKind kind, string body, CancellationToken cancellationToken)
    {
        try
        {
            var conversation = await _store.GetOrCreateForBookingSystemAsync(booking.Id, cancellationToken);
            if (conversation is null)
            {
                return;
            }

            var message = await _store.AddSystemMessageAsync(conversation.Id, kind, body, cancellationToken);
            if (message is null)
            {
                // Already emitted for this conversation (retry) — nothing new to broadcast.
                return;
            }

            var realtimeMessage = new ChatRealtimeMessage
            {
                Id = message.Id,
                ConversationId = conversation.Id,
                SenderId = null,
                SenderName = null,
                Type = ChatTokens.MessageTypeToken(MessageType.System),
                SystemKind = ChatTokens.SystemKindToken(kind),
                Body = body,
                AttachmentUrl = null,
                SentAt = message.CreatedAt
            };

            await _realtimeNotifier.MessageSentAsync(realtimeMessage, conversation.OwnerId, conversation.RenterId, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Failed to emit {Kind} chat system message for booking {BookingId}.",
                kind,
                booking.Id);
        }
    }
}
