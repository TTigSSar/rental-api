using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.DTOs;

public sealed class CurrentUserResponse
{
    public Guid Id { get; init; }
    public string Email { get; init; } = string.Empty;
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string? PhoneNumber { get; init; }
    public string? PreferredLanguage { get; init; }
    public string? AvatarUrl { get; init; }
    public DateTime CreatedAt { get; init; }
    public bool IsBlocked { get; init; }
    public UserRole Role { get; init; }

    // Self-view only (home-point model) — never appears on any other DTO. Null when the user has no home
    // point set yet.
    public HomePointResponse? HomePoint { get; init; }
}
