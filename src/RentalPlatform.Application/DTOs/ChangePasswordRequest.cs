using System.ComponentModel.DataAnnotations;

namespace RentalPlatform.Application.DTOs;

public sealed class ChangePasswordRequest
{
    [Required]
    // No MinLength here on purpose: a password either verifies or doesn't, so a length floor on
    // the CURRENT password is meaningless — and harmful, since accounts created before the
    // 8-char minimum existed (AdminBootstrapRunner/DemoContentBootstrapRunner seed data does no
    // length validation) can have shorter real passwords. A MinLength here would reject those via
    // ModelState (400 with no errorCode, untranslated message) before AuthService ever ran, and
    // would permanently lock such accounts out of changing their password. Do not restore this
    // for symmetry with NewPassword.
    [MaxLength(128)]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required]
    [MinLength(8)]
    [MaxLength(128)]
    public string NewPassword { get; set; } = string.Empty;
}
