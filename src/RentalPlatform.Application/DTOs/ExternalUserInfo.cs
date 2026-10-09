namespace RentalPlatform.Application.DTOs;

public sealed class ExternalUserInfo
{
    public string Provider { get; init; } = string.Empty;
    public string ProviderUserId { get; init; } = string.Empty;
    public string? Email { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? AvatarUrl { get; init; }

    // Google's `hd` claim (the Workspace domain the account belongs to); null for consumer
    // accounts and for Apple. Used only by the auto-link rule (ADR-028 §10).
    public string? HostedDomain { get; init; }
}
