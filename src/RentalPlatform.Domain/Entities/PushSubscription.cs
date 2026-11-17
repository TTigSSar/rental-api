namespace RentalPlatform.Domain.Entities;

// One Web Push subscription per user device (ADR-067). The endpoint is a capability URL minted by the
// browser's push service; EndpointHash (SHA-256) carries the unique index because the endpoint is too long to index.
public sealed class PushSubscription
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public byte[] EndpointHash { get; set; } = Array.Empty<byte>();
    public string P256dh { get; set; } = string.Empty;
    public string Auth { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    public User User { get; set; } = null!;
}
