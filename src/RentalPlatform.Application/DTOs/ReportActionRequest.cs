using System.ComponentModel.DataAnnotations;

namespace RentalPlatform.Application.DTOs;

/// <summary>
/// Optional body for POST /api/admin/reports/{id}/resolve and .../dismiss. Nullable at the
/// controller boundary (no body at all is valid, same convention as
/// BookingsController.Reject's RejectBookingRequest?) since the note itself is optional.
/// </summary>
public sealed class ReportActionRequest
{
    [StringLength(1000, ErrorMessage = "Note must be 1000 characters or fewer.")]
    public string? Note { get; init; }
}
