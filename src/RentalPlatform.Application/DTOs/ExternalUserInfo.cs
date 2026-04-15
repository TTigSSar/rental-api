namespace RentalPlatform.Application.DTOs;

public sealed class ExternalUserInfo
{
    public string Provider { get; init; } = string.Empty;
    public string ProviderUserId { get; init; } = string.Empty;
    public string? Email { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? AvatarUrl { get; init; }
}
