using System.ComponentModel.DataAnnotations;

namespace RentalPlatform.Application.DTOs;

// PUT /api/auth/me/name (ADR-030 section 5). The 1-100 / 0-100 limits apply AFTER trimming, so they
// are enforced in AuthService; MaxLength here only bounds the body. [Required] already rejects a
// whitespace-only first name. The last name is optional, unlike RegisterRequest.LastName.
public sealed class UpdateNameRequest
{
    [Required]
    [MaxLength(500)]
    public string FirstName { get; init; } = string.Empty;

    [MaxLength(500)]
    public string? LastName { get; init; }
}
