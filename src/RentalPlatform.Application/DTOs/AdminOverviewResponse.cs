namespace RentalPlatform.Application.DTOs;

/// <summary>
/// Admin console Phase 5: the Overview screen's stat tiles + "Queue health" panel. Every number is
/// computed straight from the database (AdminOverviewService/AdminOverviewStore) — never a
/// placeholder. Dates/durations are returned as raw values (DateTime / minutes), not pre-formatted
/// strings ("2d ago", "3.2 h") — humanising and localising that is the client's job.
/// </summary>
public sealed class AdminOverviewResponse
{
    /// <summary>Listings currently in PendingApproval.</summary>
    public int AwaitingReviewCount { get; init; }

    /// <summary>CreatedAt of the oldest PendingApproval listing; null when the queue is empty.</summary>
    public DateTime? OldestAwaitingCreatedAt { get; init; }

    /// <summary>Listings currently Approved.</summary>
    public int LiveListingCount { get; init; }

    /// <summary>
    /// Listings that became Approved (ModeratedAt, not CreatedAt — "added to the marketplace"
    /// means approved, not submitted) in the trailing 7 days.
    /// </summary>
    public int LiveListingsAddedThisWeek { get; init; }

    public int CategoryCount { get; init; }
    public int VisibleCategoryCount { get; init; }

    /// <summary>Reports currently Open.</summary>
    public int OpenReportCount { get; init; }

    /// <summary>
    /// Mean of (ModeratedAt - CreatedAt), in minutes, across listings moderated (approved or
    /// rejected) in the trailing 30 days. Null when nothing was moderated in that window.
    /// </summary>
    public double? AverageReviewTimeMinutes { get; init; }

    /// <summary>The "target &lt; 6 h" line on the Queue health panel — served from the same named
    /// constant the server itself would alert against, so the client never hardcodes it.</summary>
    public int ReviewTimeTargetHours { get; init; }
}
