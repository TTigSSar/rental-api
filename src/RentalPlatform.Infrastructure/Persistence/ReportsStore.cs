using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Infrastructure.Persistence;

public sealed class ReportsStore : IReportsStore
{
    private readonly AppDbContext _dbContext;

    public ReportsStore(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _dbContext.Users.FirstOrDefaultAsync(user => user.Id == userId, cancellationToken);

    public Task<Listing?> FindListingByIdAsync(Guid listingId, CancellationToken cancellationToken = default) =>
        _dbContext.Listings
            .Include(listing => listing.Owner)
            .FirstOrDefaultAsync(listing => listing.Id == listingId, cancellationToken);

    public Task<Conversation?> FindConversationByIdAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        _dbContext.Conversations
            .Include(conversation => conversation.Owner)
            .Include(conversation => conversation.Renter)
            .FirstOrDefaultAsync(conversation => conversation.Id == conversationId, cancellationToken);

    public Task<bool> HasOpenReportAsync(
        Guid reporterUserId, ReportTargetType targetType, Guid targetId, CancellationToken cancellationToken = default) =>
        _dbContext.Reports.AnyAsync(
            report => report.ReporterUserId == reporterUserId
                && report.TargetType == targetType
                && report.TargetId == targetId
                && report.Status == ReportStatus.Open,
            cancellationToken);

    public async Task AddAsync(Report report, CancellationToken cancellationToken = default) =>
        await _dbContext.Reports.AddAsync(report, cancellationToken);

    public async Task<ReportsPage> GetReportsPageAsync(
        ReportStatus? status,
        ReportTargetType? targetType,
        Guid? targetId,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = ApplyTarget(ApplySearch(ApplyStatus(_dbContext.Reports.AsNoTracking(), status), search), targetType, targetId);

        var totalCount = await query.CountAsync(cancellationToken);

        // Fixed sort, no client-controlled override: severity descending (High first), then
        // oldest first (CreatedAt ascending) — a High report waiting two days must outrank a Low
        // one from an hour ago.
        var items = await query
            .Include(report => report.Reporter)
            .OrderByDescending(report => report.Severity)
            .ThenBy(report => report.CreatedAt)
            .ThenBy(report => report.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new ReportsPage(items, totalCount);
    }

    public async Task<ReportStatusCounts> GetStatusCountsAsync(
        ReportTargetType? targetType, Guid? targetId, string? search, CancellationToken cancellationToken = default)
    {
        var query = ApplyTarget(ApplySearch(_dbContext.Reports.AsNoTracking(), search), targetType, targetId);

        var counts = await query
            .GroupBy(report => report.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        int CountFor(ReportStatus status) => counts.FirstOrDefault(c => c.Status == status)?.Count ?? 0;
        var all = counts.Sum(c => c.Count);

        return new ReportStatusCounts(
            CountFor(ReportStatus.Open), CountFor(ReportStatus.Resolved), CountFor(ReportStatus.Dismissed), all);
    }

    public Task<Report?> FindByIdAsync(Guid reportId, CancellationToken cancellationToken = default) =>
        _dbContext.Reports
            .Include(report => report.Reporter)
            .FirstOrDefaultAsync(report => report.Id == reportId, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, string?>> GetListingPrimaryImageUrlsAsync(
        IReadOnlyCollection<Guid> listingIds, CancellationToken cancellationToken = default)
    {
        if (listingIds.Count == 0)
        {
            return new Dictionary<Guid, string?>();
        }

        var ids = listingIds.Distinct().ToList();

        // Grouped rather than a plain ToDictionary: guards against a data inconsistency where a
        // listing somehow has more than one image flagged IsPrimary (shouldn't happen, but this
        // read must never throw over it — it just picks one deterministically).
        var rows = await _dbContext.ListingImages
            .AsNoTracking()
            .Where(image => ids.Contains(image.ListingId) && image.IsPrimary)
            .GroupBy(image => image.ListingId)
            .Select(g => new { ListingId = g.Key, Url = g.OrderBy(image => image.SortOrder).First().Url })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(row => row.ListingId, row => (string?)row.Url);
    }

    public async Task<IReadOnlyDictionary<Guid, string?>> GetUserAvatarUrlsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<Guid, string?>();
        }

        var ids = userIds.Distinct().ToList();

        var rows = await _dbContext.Users
            .AsNoTracking()
            .Where(user => ids.Contains(user.Id))
            .Select(user => new { user.Id, user.AvatarUrl })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(row => row.Id, row => row.AvatarUrl);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _dbContext.SaveChangesAsync(cancellationToken);

    private static IQueryable<Report> ApplyStatus(IQueryable<Report> query, ReportStatus? status) =>
        status is { } value ? query.Where(report => report.Status == value) : query;

    // targetId is only meaningful together with targetType (enforced in AdminReportsService, not
    // here) — a null targetType is treated as "no target filter" regardless of targetId.
    private static IQueryable<Report> ApplyTarget(IQueryable<Report> query, ReportTargetType? targetType, Guid? targetId)
    {
        if (targetType is not { } type)
        {
            return query;
        }

        query = query.Where(report => report.TargetType == type);
        return targetId is { } id ? query.Where(report => report.TargetId == id) : query;
    }

    // Search matches TargetLabel, reporter first/last/full name, reporter email, and reason
    // label (matched in memory against the small fixed catalog — see
    // ReportReasonCatalog.CodesMatchingLabel — then filtered by ReasonCode here since the label
    // itself isn't a persisted column EF Core could translate a lookup against). Case-insensitive
    // (SQL Server's default collation is CI; see SqliteTestDatabase for how tests get the same
    // behaviour against SQLite) — same convention as AdminUsersStore/AdminListingsStore.ApplySearch.
    private static IQueryable<Report> ApplySearch(IQueryable<Report> query, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return query;
        }

        var term = search.Trim();
        var matchingReasonCodes = ReportReasonCatalog.CodesMatchingLabel(term);

        return query.Where(report =>
            report.TargetLabel.Contains(term) ||
            report.Reporter.FirstName.Contains(term) ||
            report.Reporter.LastName.Contains(term) ||
            (report.Reporter.FirstName + " " + report.Reporter.LastName).Contains(term) ||
            report.Reporter.Email.Contains(term) ||
            matchingReasonCodes.Contains(report.ReasonCode));
    }
}
