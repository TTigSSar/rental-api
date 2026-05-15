using System.ComponentModel.DataAnnotations;

namespace RentalPlatform.Application.DTOs;

public sealed class RejectListingRequest
{
    [Required(ErrorMessage = "Rejection reason is required.")]
    [StringLength(1000, MinimumLength = 1, ErrorMessage = "Rejection reason must be between 1 and 1000 characters.")]
    public string Reason { get; init; } = string.Empty;
}
