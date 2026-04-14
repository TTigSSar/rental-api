namespace RentalPlatform.Domain.Entities;

public sealed class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string? PreferredLanguage { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsBlocked { get; set; }
}
