using System.ComponentModel.DataAnnotations;

namespace RentalPlatform.Application.DTOs;

/// <summary>Body for POST /api/admin/messages/threads: get-or-create the Moderation thread for this member.</summary>
public sealed class OpenAdminMessageThreadRequest
{
    [Required]
    public Guid UserId { get; init; }
}
