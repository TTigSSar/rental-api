namespace RentalPlatform.Application.DTOs;

/// <summary>
/// Query-string filter for GET /api/admin/reports. Status is a loose string ("open" | "resolved"
/// | "dismissed" | "all", case-insensitive) — same convention as AdminUserQueueFilter.Status.
/// Unrecognised or omitted values default to "open" (the state that needs the admin's attention),
/// same convention as AdminListingQueueFilter.Status defaulting to Pending. Mapping happens in
/// AdminReportsService. There is no client-controlled Sort: the queue's sort (severity descending,
/// then oldest first) is fixed.
///
/// TargetType/TargetId let the Admin Console deep-link from a specific listing/user/message to
/// its reports (e.g. the Users screen's flag count). Both optional; TargetType is a loose string
/// ("listing" | "user" | "message", case-insensitive, same parsing as ReportsService's submission
/// endpoint). Supplying TargetId without TargetType is rejected in AdminReportsService — an id is
/// only meaningful together with its type.
/// </summary>
public sealed class AdminReportQueueFilter
{
    public string? Status { get; init; }
    public string? Search { get; init; }
    public string? TargetType { get; init; }
    public Guid? TargetId { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}
