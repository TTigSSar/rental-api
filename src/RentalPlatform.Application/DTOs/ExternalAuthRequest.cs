using System.ComponentModel.DataAnnotations;

namespace RentalPlatform.Application.DTOs;

public sealed class ExternalAuthRequest
{
    [Required]
    [MaxLength(20)]
    public string Provider { get; init; } = string.Empty;

    [Required]
    public string IdToken { get; init; } = string.Empty;
}
