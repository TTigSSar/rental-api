using System.ComponentModel.DataAnnotations;

namespace RentalPlatform.Application.DTOs;

public sealed class ResendVerificationRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(320)]
    public string Email { get; set; } = string.Empty;
}
