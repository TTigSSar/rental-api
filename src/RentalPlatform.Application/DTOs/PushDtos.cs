namespace RentalPlatform.Application.DTOs;

/// <summary>GET /api/push/public-key (ADR-067): the VAPID application-server public key (base64url).</summary>
public sealed class PushPublicKeyResponse
{
    public string PublicKey { get; init; } = string.Empty;
}

/// <summary>
/// PUT /api/push/subscription body (ADR-067). Fields are nullable on purpose: a missing value must reach the
/// validator and answer <c>400 push.invalid_subscription</c>, not the framework's generic model-state error.
/// </summary>
public sealed class PushSubscriptionRequest
{
    public string? Endpoint { get; init; }
    public string? P256dh { get; init; }
    public string? Auth { get; init; }
}

/// <summary>DELETE /api/push/subscription body (ADR-067).</summary>
public sealed class PushUnsubscribeRequest
{
    public string? Endpoint { get; init; }
}
