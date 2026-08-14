using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Abstractions;

public interface IAdminOverviewStore
{
    Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every Overview stat computed with a handful of grouped/count queries — never one query per
    /// stat, never a full-entity pull to count rows. <paramref name="now"/> drives the trailing
    /// 7-day ("added this week") and trailing 30-day ("average review time") windows; passed in
    /// (rather than read from DateTime.UtcNow inside the store) so callers — and tests — control it.
    /// </summary>
    Task<AdminOverviewStats> GetStatsAsync(DateTime now, CancellationToken cancellationToken = default);

    /// <summary>The most recent moderation log entries, newest first, already clamped to <paramref name="take"/>.</summary>
    Task<IReadOnlyCollection<ModerationLogEntry>> GetRecentActivityAsync(int take, CancellationToken cancellationToken = default);

    /// <summary>
    /// Batched actor lookup for the activity feed (one query, not one per row). A user id absent
    /// from the result means that user no longer exists — callers render null name/avatar rather
    /// than dropping the row.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, User>> GetUsersByIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default);
}

/// <summary>
/// AwaitingReviewCount/OldestAwaitingCreatedAt: PendingApproval listings. LiveListingCount:
/// Approved listings. LiveListingsAddedThisWeek: Approved listings whose ModeratedAt falls in the
/// trailing 7 days. CategoryCount/VisibleCategoryCount: all categories / IsVisible categories.
/// OpenReportCount: Open reports. AverageReviewTimeMinutes: mean of (ModeratedAt - CreatedAt) in
/// minutes across listings (any status) moderated in the trailing 30 days; null when that window
/// is empty.
/// </summary>
public sealed record AdminOverviewStats(
    int AwaitingReviewCount,
    DateTime? OldestAwaitingCreatedAt,
    int LiveListingCount,
    int LiveListingsAddedThisWeek,
    int CategoryCount,
    int VisibleCategoryCount,
    int OpenReportCount,
    double? AverageReviewTimeMinutes);
