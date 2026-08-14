using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Infrastructure.Persistence;

public sealed class AdminOverviewStore : IAdminOverviewStore
{
    private readonly AppDbContext _dbContext;

    public AdminOverviewStore(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _dbContext.Users.FirstOrDefaultAsync(user => user.Id == userId, cancellationToken);

    public async Task<AdminOverviewStats> GetStatsAsync(DateTime now, CancellationToken cancellationToken = default)
    {
        var sevenDaysAgo = now.AddDays(-7);
        var thirtyDaysAgo = now.AddDays(-30);

        var awaitingReviewCount = await _dbContext.Listings
            .AsNoTracking()
            .CountAsync(listing => listing.Status == ListingStatus.PendingApproval, cancellationToken);

        // TOP-1-by-CreatedAt rather than .MinAsync(): MinAsync throws on an empty sequence, and an
        // empty pending queue is exactly the case this has to handle cleanly (null, not an
        // exception). Cast to nullable so FirstOrDefaultAsync's "no rows" result is null, not
        // default(DateTime).
        var oldestAwaitingCreatedAt = await _dbContext.Listings
            .AsNoTracking()
            .Where(listing => listing.Status == ListingStatus.PendingApproval)
            .OrderBy(listing => listing.CreatedAt)
            .Select(listing => (DateTime?)listing.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var liveListingCount = await _dbContext.Listings
            .AsNoTracking()
            .CountAsync(listing => listing.Status == ListingStatus.Approved, cancellationToken);

        // "Added to the marketplace" means approved, not submitted — ModeratedAt, not CreatedAt.
        var liveListingsAddedThisWeek = await _dbContext.Listings
            .AsNoTracking()
            .CountAsync(
                listing => listing.Status == ListingStatus.Approved
                    && listing.ModeratedAt != null
                    && listing.ModeratedAt >= sevenDaysAgo,
                cancellationToken);

        // One grouped query for both category counts rather than two separate CountAsync calls.
        var categoryCounts = await _dbContext.Categories
            .AsNoTracking()
            .GroupBy(category => category.IsVisible)
            .Select(g => new { IsVisible = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var categoryCount = categoryCounts.Sum(c => c.Count);
        var visibleCategoryCount = categoryCounts.FirstOrDefault(c => c.IsVisible)?.Count ?? 0;

        var openReportCount = await _dbContext.Reports
            .AsNoTracking()
            .CountAsync(report => report.Status == ReportStatus.Open, cancellationToken);

        // Average review time: pull just the id + two date columns (not full listing entities) for
        // whatever was moderated (approved OR rejected — "moderated" isn't status-restricted) in
        // the trailing 30 days, then average the per-row TimeSpan in memory. A DB-side AVG over a
        // TimeSpan/DATEDIFF expression would need a provider-specific translation (SQL Server vs.
        // the SQLite provider the test suite runs against) that isn't guaranteed to agree between
        // the two, so this keeps the arithmetic identical everywhere at the cost of one small,
        // narrow result set instead of a single scalar.
        var moderatedInWindow = await _dbContext.Listings
            .AsNoTracking()
            .Where(listing => listing.ModeratedAt != null && listing.ModeratedAt >= thirtyDaysAgo)
            .Select(listing => new { listing.Id, listing.CreatedAt, ModeratedAt = listing.ModeratedAt!.Value })
            .ToListAsync(cancellationToken);

        double? averageReviewTimeMinutes;
        if (moderatedInWindow.Count == 0)
        {
            averageReviewTimeMinutes = null;
        }
        else
        {
            // Known-skew fix: a listing that was rejected, fixed by the owner, and resubmitted
            // carries a fresh ModeratedAt (this review) against its ORIGINAL CreatedAt, so a
            // five-minute second look would otherwise contribute days of apparent review time —
            // and the Overview panel renders this average against a "target < 6 h" line. Use the
            // most recent PRIOR ListingRejected moderation-log entry for the listing (if any,
            // strictly before this ModeratedAt) as a tighter lower bound for when the current
            // review cycle actually started, instead of the listing's original CreatedAt. This is
            // still an approximation — the owner's own time spent editing/deciding to resubmit
            // between the prior rejection and the actual resubmission is still counted, because
            // resubmission itself isn't a logged moderation action — but it is a materially
            // tighter bound than CreatedAt, and costs one extra batched query (no new column, no
            // migration) rather than per-row lookups.
            var listingIds = moderatedInWindow.Select(row => row.Id).ToList();
            var lastRejectionByListingId = await _dbContext.ModerationLogEntries
                .AsNoTracking()
                .Where(entry => entry.TargetType == ModerationTargetType.Listing
                    && entry.Action == ModerationAction.ListingRejected
                    && listingIds.Contains(entry.TargetId))
                .GroupBy(entry => entry.TargetId)
                .Select(g => new { ListingId = g.Key, LastRejectedAt = g.Max(e => e.CreatedAt) })
                .ToDictionaryAsync(x => x.ListingId, x => x.LastRejectedAt, cancellationToken);

            averageReviewTimeMinutes = moderatedInWindow.Average(row =>
            {
                var start = row.CreatedAt;
                if (lastRejectionByListingId.TryGetValue(row.Id, out var lastRejectedAt) &&
                    lastRejectedAt > start && lastRejectedAt < row.ModeratedAt)
                {
                    // A strictly-earlier prior rejection exists — the listing was resubmitted and
                    // this is a later review, so start the clock there instead of at CreatedAt.
                    // (If lastRejectedAt == ModeratedAt, that rejection IS the current moderation
                    // event itself — self-referential, not a prior cycle — so it's excluded.)
                    start = lastRejectedAt;
                }

                return (row.ModeratedAt - start).TotalMinutes;
            });
        }

        return new AdminOverviewStats(
            awaitingReviewCount,
            oldestAwaitingCreatedAt,
            liveListingCount,
            liveListingsAddedThisWeek,
            categoryCount,
            visibleCategoryCount,
            openReportCount,
            averageReviewTimeMinutes);
    }

    public async Task<IReadOnlyCollection<ModerationLogEntry>> GetRecentActivityAsync(
        int take, CancellationToken cancellationToken = default) =>
        await _dbContext.ModerationLogEntries
            .AsNoTracking()
            .OrderByDescending(entry => entry.CreatedAt)
            .ThenByDescending(entry => entry.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, User>> GetUsersByIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<Guid, User>();
        }

        var ids = userIds.Distinct().ToList();

        return await _dbContext.Users
            .AsNoTracking()
            .Where(user => ids.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, cancellationToken);
    }
}
