namespace RentalPlatform.Application.Abstractions;

public enum PushSendOutcome
{
    Sent,

    /// <summary>The push service answered 404/410: the subscription is gone and must be deleted.</summary>
    Gone,

    /// <summary>Any other failure. Logged, never retried (ADR-067 section 5).</summary>
    Failed
}

/// <summary>One delivery. <paramref name="Topic"/> is the conversation id as Guid "N" (32 chars).</summary>
public sealed record PushDelivery(
    string Endpoint,
    string P256dh,
    string Auth,
    string PayloadJson,
    string Topic,
    int TimeToLiveSeconds);

/// <summary>The seam in front of the Web Push client so tests never touch the network. Never throws.</summary>
public interface IPushSender
{
    Task<PushSendOutcome> SendAsync(PushDelivery delivery, CancellationToken cancellationToken = default);
}
