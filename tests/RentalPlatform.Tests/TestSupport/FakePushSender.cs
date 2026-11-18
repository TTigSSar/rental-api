using System.Collections.Concurrent;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Tests.TestSupport;

// Recording IPushSender (ADR-067 seam): no network. The outcome per endpoint is scriptable.
public sealed class FakePushSender : IPushSender
{
    public ConcurrentQueue<PushDelivery> Deliveries { get; } = new();

    public Func<PushDelivery, PushSendOutcome> Outcome { get; set; } = _ => PushSendOutcome.Sent;

    public Task<PushSendOutcome> SendAsync(PushDelivery delivery, CancellationToken cancellationToken = default)
    {
        Deliveries.Enqueue(delivery);
        return Task.FromResult(Outcome(delivery));
    }
}

// Recording IChatPushNotifier for the service-level tests.
public sealed class FakeChatPushNotifier : IChatPushNotifier
{
    public List<(ChatRealtimeMessage Message, Guid OwnerId, Guid RenterId)> Calls { get; } = new();

    public Task MessageSentAsync(
        ChatRealtimeMessage message,
        Guid ownerId,
        Guid renterId,
        CancellationToken cancellationToken = default)
    {
        Calls.Add((message, ownerId, renterId));
        return Task.CompletedTask;
    }
}
