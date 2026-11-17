using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Infrastructure.Services;

/// <summary>
/// What the dispatcher needs to find the recipients. It carries no message text (ADR-067 section 2): the
/// payload is built from the conversation and the sender, never from the message.
/// </summary>
public sealed record ChatPushJob(Guid ConversationId, Guid SenderId);

/// <summary>
/// A bounded in-memory queue (ADR-067 section 5). Loss-tolerant by design: when full the OLDEST job is dropped,
/// queued jobs are lost on restart, nothing is retried. The 15-minute unread email stays the fallback.
/// </summary>
public sealed class ChatPushQueue
{
    public const int Capacity = 1000;

    private readonly Channel<ChatPushJob> _channel = Channel.CreateBounded<ChatPushJob>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    public bool TryEnqueue(ChatPushJob job) => _channel.Writer.TryWrite(job);

    public ChannelReader<ChatPushJob> Reader => _channel.Reader;
}

// Enqueues only; never throws, never blocks (IChatPushNotifier contract).
public sealed class ChatPushNotifier : IChatPushNotifier
{
    private readonly ChatPushQueue _queue;
    private readonly IWebPushSettings _settings;
    private readonly ILogger<ChatPushNotifier> _logger;

    public ChatPushNotifier(ChatPushQueue queue, IWebPushSettings settings, ILogger<ChatPushNotifier> logger)
    {
        _queue = queue;
        _settings = settings;
        _logger = logger;
    }

    public Task MessageSentAsync(
        ChatRealtimeMessage message,
        Guid ownerId,
        Guid renterId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // System lines never push (ADR-067 section 9); they have no human sender anyway.
            if (!_settings.IsEnabled
                || message.SenderId is not { } senderId
                || string.Equals(message.Type, "system", StringComparison.OrdinalIgnoreCase))
            {
                return Task.CompletedTask;
            }

            _queue.TryEnqueue(new ChatPushJob(message.ConversationId, senderId));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not queue a chat push for conversation {ConversationId}.", message.ConversationId);
        }

        return Task.CompletedTask;
    }
}
