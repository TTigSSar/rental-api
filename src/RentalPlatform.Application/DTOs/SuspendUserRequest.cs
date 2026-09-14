using System.ComponentModel.DataAnnotations;

namespace RentalPlatform.Application.DTOs;

/// <summary>
/// Optional body for POST /api/admin/users/{id}/suspend. Nullable at the controller boundary (no
/// body at all is valid — same convention as ReportActionRequest) since the reason itself is
/// optional; an existing caller that posts no body keeps working unchanged.
/// </summary>
public sealed class SuspendUserRequest
{
    [StringLength(200, ErrorMessage = "Reason must be 200 characters or fewer.")]
    public string? Reason { get; init; }
}
