using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.DTOs;

/// <summary>The created report, returned from POST /api/reports.</summary>
public sealed class ReportResponse
{
    public Guid Id { get; init; }
    public ReportTargetType TargetType { get; init; }
    public Guid TargetId { get; init; }
    public string TargetLabel { get; init; } = string.Empty;
    public string ReasonCode { get; init; } = string.Empty;
    public string? Detail { get; init; }
    public ReportSeverity Severity { get; init; }
    public ReportStatus Status { get; init; }
    public DateTime CreatedAt { get; init; }
}
