using System.ComponentModel.DataAnnotations;
using RentalPlatform.Application.Common;

namespace RentalPlatform.Application.DTOs;

/// <summary>
/// POST /api/reports body. TargetType is a loose string ("listing" | "user" | "message",
/// case-insensitive) rather than the domain enum directly — same convention as
/// AdminListingQueueFilter.Status — mapped in ReportsService, which is also the layer that owns
/// the deeper business validation (reason-code validity, target existence, self-report and
/// duplicate-open-report guards) since those need DB lookups a DataAnnotations attribute can't do.
/// </summary>
public sealed class CreateReportRequest
{
    [Required(ErrorMessage = "Target type is required.")]
    public string TargetType { get; init; } = string.Empty;

    [Required(ErrorMessage = "Target id is required.")]
    public Guid TargetId { get; init; }

    [Required(ErrorMessage = "Reason is required.")]
    public string ReasonCode { get; init; } = string.Empty;

    [StringLength(ReportReasonCatalog.MaxDetailLength, ErrorMessage = "Detail must be 2000 characters or fewer.")]
    public string? Detail { get; init; }
}
