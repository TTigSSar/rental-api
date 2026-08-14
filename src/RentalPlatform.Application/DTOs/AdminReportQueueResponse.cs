namespace RentalPlatform.Application.DTOs;

public sealed class AdminReportQueueResponse
{
    public IReadOnlyCollection<AdminReportRowResponse> Items { get; init; } = Array.Empty<AdminReportRowResponse>();
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int TotalPages { get; init; }
    public AdminReportCounts Counts { get; init; } = new();
}

/// <summary>
/// Search-filtered (not status-filtered) tab counts — same convention as
/// AdminListingQueueCounts/AdminUserQueueSummary: the header numbers stay stable as the admin
/// switches status tabs with the same search term applied.
/// </summary>
public sealed class AdminReportCounts
{
    public int Open { get; init; }
    public int Resolved { get; init; }
    public int Dismissed { get; init; }
    public int All { get; init; }
}
