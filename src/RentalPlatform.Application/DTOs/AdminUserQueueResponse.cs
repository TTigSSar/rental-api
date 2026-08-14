namespace RentalPlatform.Application.DTOs;

public sealed class AdminUserQueueResponse
{
    public IReadOnlyCollection<AdminUserSummaryResponse> Items { get; init; } = Array.Empty<AdminUserSummaryResponse>();
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int TotalPages { get; init; }
    public AdminUserQueueSummary Summary { get; init; } = new();
}

/// <summary>
/// Search-filtered (not status-filtered) aggregate counts for the design's stat row — same
/// convention as AdminListingQueueCounts/AdminCategoriesSummary: the header numbers stay stable as
/// the admin switches status tabs with the same search term applied.
/// </summary>
public sealed class AdminUserQueueSummary
{
    public int TotalUsers { get; init; }
    public int VerifiedCount { get; init; }
    public int PendingCount { get; init; }
    public int SuspendedCount { get; init; }
}
