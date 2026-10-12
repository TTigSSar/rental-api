namespace RentalPlatform.Application.DTOs;

public sealed class ExternalNonceResponse
{
    public string Nonce { get; init; } = string.Empty;

    // UTC.
    public DateTime ExpiresAt { get; init; }
}
