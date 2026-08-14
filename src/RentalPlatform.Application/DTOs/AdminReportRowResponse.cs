using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.DTOs;

/// <summary>
/// One row of the admin Reports queue — also the shape returned by GET /api/admin/reports/{id}
/// and by each triage mutation (resolve/dismiss/reopen), always freshly recomputed after a write.
/// TargetImageUrl is resolved server-side (listing primary image or user avatar; null for a
/// Message target) so the client needs no second lookup.
/// </summary>
public sealed class AdminReportRowResponse
{
    public Guid Id { get; init; }

    public ReportTargetType TargetType { get; init; }
    public Guid TargetId { get; init; }
    public string TargetLabel { get; init; } = string.Empty;
    public string? TargetImageUrl { get; init; }

    public string ReasonCode { get; init; } = string.Empty;
    public string? Detail { get; init; }
    public ReportSeverity Severity { get; init; }
    public ReportStatus Status { get; init; }

    public DateTime CreatedAt { get; init; }

    public Guid ReporterId { get; init; }
    public string ReporterFirstName { get; init; } = string.Empty;
    public string ReporterLastName { get; init; } = string.Empty;
    public string ReporterEmail { get; init; } = string.Empty;
    public string? ReporterAvatarUrl { get; init; }

    public DateTime? ResolvedAt { get; init; }
    public string? ResolutionNote { get; init; }
}
