using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Abstractions;

public interface IAdminListingsStore
{
    Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Returns all PendingApproval listings with Owner, Category, and Images loaded.</summary>
    Task<IReadOnlyCollection<Listing>> GetPendingListingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the listing by id with Owner and Category loaded (tracked for update).</summary>
    Task<Listing?> FindListingByIdAsync(Guid listingId, CancellationToken cancellationToken = default);

    /// <summary>Returns the listing by id with Owner, Category, and Images loaded (tracked for update).</summary>
    Task<Listing?> FindListingWithImagesByIdAsync(Guid listingId, CancellationToken cancellationToken = default);

    Task<Category?> FindCategoryByIdAsync(Guid categoryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// One status-filtered, search-filtered page of listings (Owner, Category, Images loaded),
    /// plus the total count of that same filtered set. Pending sorts oldest-first (FIFO queue);
    /// Approved/Rejected sort newest-moderated-first.
    /// </summary>
    Task<AdminListingsPage> GetListingsPageAsync(
        ListingStatus status,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>Counts of Pending/Approved/Rejected listings, filtered by the same search term (not by status).</summary>
    Task<AdminListingQueueCounts> GetStatusCountsAsync(string? search, CancellationToken cancellationToken = default);

    /// <summary>
    /// Approved-listing count and completed-rental count per owner, for the given owner ids —
    /// two grouped queries total, never one query per owner. Owners absent from a page's data
    /// are simply absent from the result; callers treat a missing key as all-zero.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, OwnerListingStats>> GetOwnerListingStatsAsync(
        IReadOnlyCollection<Guid> ownerIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Admin console Phase 6 ("Needs category fix"): every CategoryKeyword row, joined with its
    /// category's Name/DisplayOrder. The table is small and changes rarely, so callers load it
    /// once per request and match every listing on the page against it in memory — never one
    /// query per listing.
    /// </summary>
    Task<IReadOnlyCollection<CategoryKeywordEntry>> GetCategoryKeywordsAsync(CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

public sealed record AdminListingsPage(IReadOnlyCollection<Listing> Items, int TotalCount);

/// <summary>OpenReportCount is the count of Open reports filed against the owner (ReportTargetType.User,
/// TargetId == the owner's id) — only consumed by AdminListingDetailResponse.OwnerOpenReportCount today,
/// but computed for every owner in the batch here (same one-grouped-query convention as the other two
/// fields) rather than a separate single-owner method.</summary>
public sealed record OwnerListingStats(int ApprovedListingCount, int CompletedRentalCount, int OpenReportCount);

/// <summary>One CategoryKeyword row denormalised with its owning category's Name/DisplayOrder — everything
/// AdminListingsService's suggestion-rule scoring needs without a second lookup.</summary>
public sealed record CategoryKeywordEntry(Guid CategoryId, string CategoryName, int CategoryDisplayOrder, string Keyword);
