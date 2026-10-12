using System.ComponentModel.DataAnnotations;

namespace RentalPlatform.Application.DTOs;

// PUT /api/auth/me/phone (ADR-030 section 8). Same rules as RegisterRequest.PhoneNumber.
public sealed class UpdatePhoneRequest
{
    [Required]
    [RegularExpression(RegisterRequest.PhoneNumberPattern, ErrorMessage = "Enter a valid phone number.")]
    [MaxLength(32)]
    public string PhoneNumber { get; init; } = string.Empty;
}
