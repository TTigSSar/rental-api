namespace RentalPlatform.Application.DTOs;

public sealed class AuthResponse
{
    public string AccessToken { get; init; } = string.Empty;
    public CurrentUserResponse User { get; init; } = new();
}
