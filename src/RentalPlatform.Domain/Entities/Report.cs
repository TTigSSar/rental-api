using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Domain.Entities;

/// <summary>
/// A community-submitted report against a listing, a user, or a message (booking conversation),
/// triaged by an admin on the Reports console. TargetLabel is a denormalised, human-readable
/// snapshot (listing title / user full name / conversation label) resolved server-side at
/// creation time — never taken from the client — so the queue still reads correctly if the
/// target itself is later deleted. Severity is likewise derived server-side from ReasonCode via
/// ReportReasonCatalog, never client-supplied.
/// </summary>
public sealed class Report
{
    public Guid Id { get; set; }

    public ReportTargetType TargetType { get; set; }
    public Guid TargetId { get; set; }
    public string TargetLabel { get; set; } = string.Empty;

    public Guid ReporterUserId { get; set; }

    public string ReasonCode { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public ReportSeverity Severity { get; set; }
    public ReportStatus Status { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? ResolvedAt { get; set; }
    public Guid? ResolvedByUserId { get; set; }
    public string? ResolutionNote { get; set; }

    public User Reporter { get; set; } = null!;
    public User? ResolvedByUser { get; set; }
}
