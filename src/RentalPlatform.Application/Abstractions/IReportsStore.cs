using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Abstractions;

/// <summary>
/// Backs both the user-facing report submission flow (ReportsService) and the admin triage
/// queue (AdminReportsService) — a single store, same convention as IModerationLogStore being
/// shared across admin services, since both sides operate on the same Report entity.
/// </summary>
public interface IReportsStore
{
    // ---- Shared lookups ----

    Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Returns the listing by id with Owner loaded (for the self-report check + title label).</summary>
    Task<Listing?> FindListingByIdAsync(Guid listingId, CancellationToken cancellationToken = default);

    /// <summary>Returns the conversation by id with Owner and Renter loaded (for the counterpart label).</summary>
    Task<Conversation?> FindConversationByIdAsync(Guid conversationId, CancellationToken cancellationToken = default);

    // ---- User-facing create flow ----

    /// <summary>True when this reporter already has an Open report against this exact target — the anti-abuse guard.</summary>
    Task<bool> HasOpenReportAsync(
        Guid reporterUserId, ReportTargetType targetType, Guid targetId, CancellationToken cancellationToken = default);

    Task AddAsync(Report report, CancellationToken cancellationToken = default);

    // ---- Admin queue / detail / mutations ----

    /// <summary>
    /// One status-filtered (null = all), search-filtered, target-filtered page of reports
    /// (Reporter loaded), plus the total count of that same filtered set. Always sorted severity
    /// descending, then oldest (CreatedAt ascending) first — the admin queue has no
    /// client-controlled sort. targetId is only meaningful together with targetType; callers must
    /// not pass targetId without targetType (enforced in AdminReportsService, not here).
    /// </summary>
    Task<ReportsPage> GetReportsPageAsync(
        ReportStatus? status, ReportTargetType? targetType, Guid? targetId, string? search, int page, int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>Open/Resolved/Dismissed/All counts, filtered by the same search term and target filter
    /// (not by status).</summary>
    Task<ReportStatusCounts> GetStatusCountsAsync(
        ReportTargetType? targetType, Guid? targetId, string? search, CancellationToken cancellationToken = default);

    /// <summary>Tracked lookup by id, with Reporter loaded — used by GetById and every mutation endpoint.</summary>
    Task<Report?> FindByIdAsync(Guid reportId, CancellationToken cancellationToken = default);

    /// <summary>Primary image URL per listing id, for TargetImageUrl resolution on Listing-target rows.</summary>
    Task<IReadOnlyDictionary<Guid, string?>> GetListingPrimaryImageUrlsAsync(
        IReadOnlyCollection<Guid> listingIds, CancellationToken cancellationToken = default);

    /// <summary>Avatar URL per user id, for TargetImageUrl resolution on User-target rows.</summary>
    Task<IReadOnlyDictionary<Guid, string?>> GetUserAvatarUrlsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

public sealed record ReportsPage(IReadOnlyCollection<Report> Items, int TotalCount);

public sealed record ReportStatusCounts(int Open, int Resolved, int Dismissed, int All);
