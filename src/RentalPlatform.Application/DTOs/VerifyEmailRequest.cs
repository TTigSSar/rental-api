using System.ComponentModel.DataAnnotations;

namespace RentalPlatform.Application.DTOs;

// The link token AND the account password (ADR-028 §3): the link alone is not a login.
// Password is verification-side input, so it carries no 72-byte cap (see PasswordPolicy).
public sealed class VerifyEmailRequest
{
    [Required]
    [MaxLength(256)]
    public string Token { get; set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string Password { get; set; } = string.Empty;
}
