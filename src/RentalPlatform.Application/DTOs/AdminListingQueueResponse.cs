namespace RentalPlatform.Application.DTOs;

public sealed class AdminListingQueueResponse
{
    public IReadOnlyCollection<AdminListingSummaryResponse> Items { get; init; } = Array.Empty<AdminListingSummaryResponse>();
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int TotalPages { get; init; }
    public AdminListingQueueCounts Counts { get; init; } = new();
}

/// <summary>
/// Unfiltered-by-status, filtered-by-search tab counts, so the queue's tab badges always match
/// what the admin would see if they switched tabs with the current search still applied.
/// </summary>
public sealed class AdminListingQueueCounts
{
    public int Pending { get; init; }
    public int Approved { get; init; }
    public int Rejected { get; init; }
}
