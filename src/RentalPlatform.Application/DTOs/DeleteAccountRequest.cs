using System.ComponentModel.DataAnnotations;

namespace RentalPlatform.Application.DTOs;

// ADR-056. A password account re-enters its password; an account without one (Google only) types its
// email. Which field is required depends on the stored account, so it is decided in the service and
// answered with account.invalid_confirmation, not by a model-validation rule.
public sealed class DeleteAccountRequest
{
    [MaxLength(128)]
    public string? Password { get; set; }

    [MaxLength(320)]
    public string? Email { get; set; }
}
