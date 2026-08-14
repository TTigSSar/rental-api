using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Abstractions;

public interface IAdminUsersStore
{
    /// <summary>Tracked lookup by id — used both for the EnsureAdminAsync auth re-check and as the
    /// mutation target for verify/suspend/reactivate.</summary>
    Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// One status-filtered, search-filtered, sorted page of users, plus the total count of that
    /// same filtered set. ListingCount/RentalCount are computed inline via correlated-subquery
    /// counts in the same SQL statement (the same pattern ListingsQueryService uses for
    /// ReviewCount/RatingSum) rather than a separate GroupBy-then-dictionary pass: sorting by
    /// "listings" or "rentals" needs the aggregate to drive the ORDER BY/OFFSET itself, which a
    /// post-page batched lookup (AdminListingsStore's owner-stats convention) cannot do.
    /// </summary>
    Task<AdminUsersPage> GetUsersPageAsync(
        UserAccountStatus? status,
        string? search,
        AdminUserSort sort,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>Single-row equivalent of the page projection above (same ListingCount/RentalCount
    /// derivation) — used by GetById and by every mutation endpoint to return the fresh row after
    /// a write.</summary>
    Task<AdminUserRow?> FindRowByIdAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Total/Verified(active)/Pending/Suspended counts, filtered by the same search term
    /// (not by status) — same convention as AdminListingsStore.GetStatusCountsAsync.</summary>
    Task<AdminUserStatusCounts> GetStatusCountsAsync(string? search, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

public enum AdminUserSort
{
    Name,
    Newest,
    Listings,
    Rentals,
    Flags
}

public sealed record AdminUsersPage(IReadOnlyCollection<AdminUserRow> Items, int TotalCount);

/// <summary>A user row with its computed activity counts. Status/MarketplaceRole are derived from
/// these fields by the service (pure logic, no DB needed), not by the store. FlagCount is the
/// count of Open reports filed against this user (ReportTargetType.User, TargetId == this user's
/// id) — computed the same correlated-subquery way as ListingCount/RentalCount.</summary>
public sealed record AdminUserRow(
    Guid Id,
    string Email,
    string FirstName,
    string LastName,
    string? AvatarUrl,
    UserRole Role,
    bool IsBlocked,
    bool IsIdConfirmed,
    DateTime CreatedAt,
    int ListingCount,
    int RentalCount,
    int FlagCount);

public sealed record AdminUserStatusCounts(int Total, int Verified, int Pending, int Suspended);
