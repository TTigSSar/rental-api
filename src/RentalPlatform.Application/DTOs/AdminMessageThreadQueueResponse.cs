namespace RentalPlatform.Application.DTOs;

public sealed class AdminMessageThreadQueueResponse
{
    public IReadOnlyCollection<AdminMessageThreadResponse> Items { get; init; } = Array.Empty<AdminMessageThreadResponse>();
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int TotalPages { get; init; }
    public AdminMessageThreadCounts Counts { get; init; } = new();
}

/// <summary>
/// Search-filtered (not pill-filtered) totals for the design's three filter pills — same
/// convention as AdminReportCounts/AdminUserQueueSummary: the header numbers stay stable as the
/// admin switches the All/Unread/NeedsReply pill with the same search term applied. Also backs the
/// admin shell's nav unread badge (<see cref="Unread"/>), which otherwise only sees one loaded page.
/// </summary>
public sealed class AdminMessageThreadCounts
{
    public int All { get; init; }
    public int Unread { get; init; }
    public int NeedsReply { get; init; }
}
