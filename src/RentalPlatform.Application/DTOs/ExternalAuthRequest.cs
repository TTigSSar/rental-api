using System.ComponentModel.DataAnnotations;

namespace RentalPlatform.Application.DTOs;

public sealed class ExternalAuthRequest
{
    [Required]
    [MaxLength(20)]
    public string Provider { get; init; } = string.Empty;

    [Required]
    public string IdToken { get; init; } = string.Empty;

    // en | hy | ru; anything else is treated as null (ADR-030 section 6). Used only when a user is
    // created or a pending registration is converted.
    public string? PreferredLanguage { get; init; }
}
