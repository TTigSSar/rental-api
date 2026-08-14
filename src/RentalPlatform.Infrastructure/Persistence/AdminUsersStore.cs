using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Infrastructure.Persistence;

public sealed class AdminUsersStore : IAdminUsersStore
{
    private readonly AppDbContext _dbContext;

    public AdminUsersStore(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _dbContext.Users.FirstOrDefaultAsync(user => user.Id == userId, cancellationToken);

    public async Task<AdminUsersPage> GetUsersPageAsync(
        UserAccountStatus? status,
        string? search,
        AdminUserSort sort,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = ApplySearch(ApplyStatus(_dbContext.Users.AsNoTracking(), status), search);

        var totalCount = await query.CountAsync(cancellationToken);

        query = sort switch
        {
            AdminUserSort.Newest => query.OrderByDescending(u => u.CreatedAt).ThenBy(u => u.Id),
            // Sorting by an aggregate needs the count to drive the ORDER BY/OFFSET itself, so it's
            // a correlated-subquery expression here rather than a post-page batched lookup (see
            // the interface doc). Duplicated verbatim in the projection below for the same reason
            // ListingsQueryService duplicates its Haversine formula: EF Core only translates a
            // method call it can inline into the expression tree.
            AdminUserSort.Listings => query
                .OrderByDescending(u => _dbContext.Listings.Count(l => l.OwnerId == u.Id && l.Status == ListingStatus.Approved))
                .ThenBy(u => u.FirstName).ThenBy(u => u.LastName).ThenBy(u => u.Id),
            AdminUserSort.Rentals => query
                .OrderByDescending(u => _dbContext.Bookings.Count(b => b.RenterId == u.Id && b.Status == BookingStatus.Completed))
                .ThenBy(u => u.FirstName).ThenBy(u => u.LastName).ThenBy(u => u.Id),
            // FlagCount is the count of Open reports filed against this user — the aggregate
            // drives the ORDER BY/OFFSET itself, same reason Listings/Rentals above use a
            // correlated-subquery expression rather than a post-page batched lookup.
            AdminUserSort.Flags => query
                .OrderByDescending(u => _dbContext.Reports.Count(
                    r => r.TargetType == ReportTargetType.User && r.TargetId == u.Id && r.Status == ReportStatus.Open))
                .ThenBy(u => u.FirstName).ThenBy(u => u.LastName).ThenBy(u => u.Id),
            _ => query.OrderBy(u => u.FirstName).ThenBy(u => u.LastName).ThenBy(u => u.Id)
        };

        var rows = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new
            {
                u.Id,
                u.Email,
                u.FirstName,
                u.LastName,
                u.AvatarUrl,
                u.Role,
                u.IsBlocked,
                u.IsIdConfirmed,
                u.CreatedAt,
                ListingCount = _dbContext.Listings.Count(l => l.OwnerId == u.Id && l.Status == ListingStatus.Approved),
                RentalCount = _dbContext.Bookings.Count(b => b.RenterId == u.Id && b.Status == BookingStatus.Completed),
                FlagCount = _dbContext.Reports.Count(
                    r => r.TargetType == ReportTargetType.User && r.TargetId == u.Id && r.Status == ReportStatus.Open)
            })
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(r => new AdminUserRow(
                r.Id, r.Email, r.FirstName, r.LastName, r.AvatarUrl, r.Role, r.IsBlocked, r.IsIdConfirmed, r.CreatedAt,
                r.ListingCount, r.RentalCount, r.FlagCount))
            .ToList();

        return new AdminUsersPage(items, totalCount);
    }

    public async Task<AdminUserRow?> FindRowByIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var r = await _dbContext.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new
            {
                u.Id,
                u.Email,
                u.FirstName,
                u.LastName,
                u.AvatarUrl,
                u.Role,
                u.IsBlocked,
                u.IsIdConfirmed,
                u.CreatedAt,
                ListingCount = _dbContext.Listings.Count(l => l.OwnerId == u.Id && l.Status == ListingStatus.Approved),
                RentalCount = _dbContext.Bookings.Count(b => b.RenterId == u.Id && b.Status == BookingStatus.Completed),
                FlagCount = _dbContext.Reports.Count(
                    r => r.TargetType == ReportTargetType.User && r.TargetId == u.Id && r.Status == ReportStatus.Open)
            })
            .FirstOrDefaultAsync(cancellationToken);

        return r is null
            ? null
            : new AdminUserRow(r.Id, r.Email, r.FirstName, r.LastName, r.AvatarUrl, r.Role, r.IsBlocked, r.IsIdConfirmed, r.CreatedAt, r.ListingCount, r.RentalCount, r.FlagCount);
    }

    public async Task<AdminUserStatusCounts> GetStatusCountsAsync(string? search, CancellationToken cancellationToken = default)
    {
        var query = ApplySearch(_dbContext.Users.AsNoTracking(), search);

        // One grouped query for all four counts rather than four separate CountAsync round trips
        // — same convention as ReportsStore.GetStatusCountsAsync / AdminOverviewStore's category
        // counts. Derivation kept identical to ApplyStatus/DeriveStatus: Suspended (IsBlocked)
        // takes priority; else Active (IsIdConfirmed); else Pending.
        var counts = await query
            .GroupBy(u => new { u.IsBlocked, u.IsIdConfirmed })
            .Select(g => new { g.Key.IsBlocked, g.Key.IsIdConfirmed, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var total = counts.Sum(c => c.Count);
        var suspended = counts.Where(c => c.IsBlocked).Sum(c => c.Count);
        var pending = counts.Where(c => !c.IsBlocked && !c.IsIdConfirmed).Sum(c => c.Count);
        var verified = counts.Where(c => !c.IsBlocked && c.IsIdConfirmed).Sum(c => c.Count);

        return new AdminUserStatusCounts(total, verified, pending, suspended);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _dbContext.SaveChangesAsync(cancellationToken);

    private static IQueryable<User> ApplyStatus(IQueryable<User> query, UserAccountStatus? status) => status switch
    {
        UserAccountStatus.Suspended => query.Where(u => u.IsBlocked),
        UserAccountStatus.Pending => query.Where(u => !u.IsBlocked && !u.IsIdConfirmed),
        UserAccountStatus.Active => query.Where(u => !u.IsBlocked && u.IsIdConfirmed),
        _ => query
    };

    // Search matches first name, last name, "first last", or email, case-insensitive (SQL
    // Server's default collation is CI; see SqliteTestDatabase for how tests get the same
    // behaviour against SQLite) — same convention as AdminListingsStore.ApplySearch.
    private static IQueryable<User> ApplySearch(IQueryable<User> query, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return query;
        }

        var term = search.Trim();
        return query.Where(u =>
            u.FirstName.Contains(term) ||
            u.LastName.Contains(term) ||
            (u.FirstName + " " + u.LastName).Contains(term) ||
            u.Email.Contains(term));
    }
}
