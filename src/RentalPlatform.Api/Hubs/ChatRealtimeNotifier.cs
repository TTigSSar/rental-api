using Microsoft.AspNetCore.SignalR;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Api.Hubs;

/// <summary>
/// SignalR-backed implementation of <see cref="IChatRealtimeNotifier"/>. Lives in Api (not
/// Infrastructure/Application) because it depends on <see cref="IHubContext{THub}"/>, which is
/// an ASP.NET Core hosting concern — Application must stay free of any SignalR reference.
/// Never throws: realtime delivery is best-effort, mirroring <see cref="IEmailService"/>.
/// </summary>
public sealed class ChatRealtimeNotifier : IChatRealtimeNotifier
{
    private const string MessageReceivedEvent = "messageReceived";
    private const string ConversationReadEvent = "conversationRead";

    private readonly IHubContext<ChatHub> _hubContext;
    private readonly ILogger<ChatRealtimeNotifier> _logger;

    public ChatRealtimeNotifier(IHubContext<ChatHub> hubContext, ILogger<ChatRealtimeNotifier> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task MessageSentAsync(
        ChatRealtimeMessage message,
        Guid ownerId,
        Guid renterId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _hubContext.Clients
                .Users(new[] { ownerId.ToString(), renterId.ToString() })
                .SendAsync(MessageReceivedEvent, message, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to push realtime chat message {MessageId} for conversation {ConversationId}",
                message.Id,
                message.ConversationId);
        }
    }

    public async Task ConversationReadAsync(
        Guid conversationId,
        Guid readerUserId,
        Guid otherUserId,
        DateTime readAtUtc,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _hubContext.Clients
                .Users(new[] { readerUserId.ToString(), otherUserId.ToString() })
                .SendAsync(
                    ConversationReadEvent,
                    new { conversationId, readerUserId, readAtUtc },
                    cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to push realtime conversation-read event for conversation {ConversationId}",
                conversationId);
        }
    }
}
