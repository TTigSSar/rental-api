using System.ComponentModel.DataAnnotations;

namespace RentalPlatform.Application.DTOs;

public sealed class UpdateChatEmailsRequest
{
    // Nullable + [Required] so a missing field is a 400, not a silent "false".
    [Required]
    public bool? Enabled { get; init; }
}
