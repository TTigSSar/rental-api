using System.ComponentModel.DataAnnotations;

namespace RentalPlatform.Application.DTOs;

// ADR-035. No [EmailAddress]/[Required] on purpose: a malformed or empty address must answer the same
// 202 as an unknown one, so the shape of the input can never tell a caller anything.
public sealed class ForgotPasswordRequest
{
    [MaxLength(320)]
    public string? Email { get; set; }

    // The client's current UI language; wins over the stored one for this email only.
    [MaxLength(16)]
    public string? PreferredLanguage { get; set; }
}

public sealed class ResetPasswordCheckRequest
{
    [Required]
    [MaxLength(256)]
    public string Token { get; set; } = string.Empty;
}

// NewPassword: the 8-character minimum is a DataAnnotation like RegisterRequest/ChangePasswordRequest;
// the 72-byte cap is enforced in the service (auth.password_too_long), ADR-021 amendment 2026-09-27.
public sealed class ResetPasswordRequest
{
    [Required]
    [MaxLength(256)]
    public string Token { get; set; } = string.Empty;

    [Required]
    [MinLength(8)]
    [MaxLength(128)]
    public string NewPassword { get; set; } = string.Empty;
}
