using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Infrastructure.Persistence;

public sealed class AdminListingsStore : IAdminListingsStore
{
    private readonly AppDbContext _dbContext;

    public AdminListingsStore(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _dbContext.Users.FirstOrDefaultAsync(user => user.Id == userId, cancellationToken);

    public async Task<IReadOnlyCollection<Listing>> GetPendingListingsAsync(CancellationToken cancellationToken = default) =>
        await _dbContext.Listings
            .AsNoTracking()
            .Where(listing => listing.Status == ListingStatus.PendingApproval)
            .Include(listing => listing.Owner)
            .Include(listing => listing.Category)
            .Include(listing => listing.Images.OrderByDescending(i => i.IsPrimary).ThenBy(i => i.SortOrder))
            .AsSplitQuery()
            .OrderBy(listing => listing.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<Listing?> FindListingByIdAsync(Guid listingId, CancellationToken cancellationToken = default) =>
        _dbContext.Listings
            .Include(listing => listing.Owner)
            .Include(listing => listing.Category)
            .FirstOrDefaultAsync(listing => listing.Id == listingId, cancellationToken);

    public Task<Listing?> FindListingWithImagesByIdAsync(Guid listingId, CancellationToken cancellationToken = default) =>
        _dbContext.Listings
            .Include(listing => listing.Owner)
            .Include(listing => listing.Category)
            .Include(listing => listing.Images.OrderByDescending(i => i.IsPrimary).ThenBy(i => i.SortOrder))
            .AsSplitQuery()
            .FirstOrDefaultAsync(listing => listing.Id == listingId, cancellationToken);

    public Task<Category?> FindCategoryByIdAsync(Guid categoryId, CancellationToken cancellationToken = default) =>
        _dbContext.Categories.FirstOrDefaultAsync(category => category.Id == categoryId, cancellationToken);

    public async Task<AdminListingsPage> GetListingsPageAsync(
        ListingStatus status,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = ApplySearch(_dbContext.Listings.AsNoTracking().Where(listing => listing.Status == status), search);

        var totalCount = await query.CountAsync(cancellationToken);

        // Pending is worked FIFO (oldest waiting at the top); Approved/Rejected show the most
        // recently moderated first. ModeratedAt is always set once a listing leaves Pending, but
        // the CreatedAt fallback keeps the ordering well-defined even if that were ever not true.
        query = status == ListingStatus.PendingApproval
            ? query.OrderBy(listing => listing.CreatedAt)
            : query.OrderByDescending(listing => listing.ModeratedAt ?? listing.CreatedAt);

        var items = await query
            .Include(listing => listing.Owner)
            .Include(listing => listing.Category)
            .Include(listing => listing.Images.OrderByDescending(i => i.IsPrimary).ThenBy(i => i.SortOrder))
            .AsSplitQuery()
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new AdminListingsPage(items, totalCount);
    }

    public async Task<AdminListingQueueCounts> GetStatusCountsAsync(string? search, CancellationToken cancellationToken = default)
    {
        var query = ApplySearch(_dbContext.Listings.AsNoTracking(), search);

        var counts = await query
            .Where(listing =>
                listing.Status == ListingStatus.PendingApproval ||
                listing.Status == ListingStatus.Approved ||
                listing.Status == ListingStatus.Rejected)
            .GroupBy(listing => listing.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        int CountFor(ListingStatus status) => counts.FirstOrDefault(c => c.Status == status)?.Count ?? 0;

        return new AdminListingQueueCounts
        {
            Pending = CountFor(ListingStatus.PendingApproval),
            Approved = CountFor(ListingStatus.Approved),
            Rejected = CountFor(ListingStatus.Rejected)
        };
    }

    public async Task<IReadOnlyDictionary<Guid, OwnerListingStats>> GetOwnerListingStatsAsync(
        IReadOnlyCollection<Guid> ownerIds, CancellationToken cancellationToken = default)
    {
        if (ownerIds.Count == 0)
        {
            return new Dictionary<Guid, OwnerListingStats>();
        }

        var ids = ownerIds.Distinct().ToList();

        var listingCounts = await _dbContext.Listings
            .AsNoTracking()
            .Where(listing => ids.Contains(listing.OwnerId) && listing.Status == ListingStatus.Approved)
            .GroupBy(listing => listing.OwnerId)
            .Select(g => new { OwnerId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var completedRentalCounts = await _dbContext.Bookings
            .AsNoTracking()
            .Where(booking => booking.Status == BookingStatus.Completed && ids.Contains(booking.Listing.OwnerId))
            .GroupBy(booking => booking.Listing.OwnerId)
            .Select(g => new { OwnerId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        // Open reports filed against the owner (ReportTargetType.User, TargetId == owner id).
        var openReportCounts = await _dbContext.Reports
            .AsNoTracking()
            .Where(report =>
                report.TargetType == ReportTargetType.User &&
                report.Status == ReportStatus.Open &&
                ids.Contains(report.TargetId))
            .GroupBy(report => report.TargetId)
            .Select(g => new { OwnerId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var listingCountByOwner = listingCounts.ToDictionary(x => x.OwnerId, x => x.Count);
        var completedRentalsByOwner = completedRentalCounts.ToDictionary(x => x.OwnerId, x => x.Count);
        var openReportsByOwner = openReportCounts.ToDictionary(x => x.OwnerId, x => x.Count);

        var result = new Dictionary<Guid, OwnerListingStats>();
        foreach (var ownerId in ids)
        {
            listingCountByOwner.TryGetValue(ownerId, out var listingCount);
            completedRentalsByOwner.TryGetValue(ownerId, out var completedRentals);
            openReportsByOwner.TryGetValue(ownerId, out var openReports);
            result[ownerId] = new OwnerListingStats(listingCount, completedRentals, openReports);
        }

        return result;
    }

    public async Task<IReadOnlyCollection<CategoryKeywordEntry>> GetCategoryKeywordsAsync(
        CancellationToken cancellationToken = default) =>
        await _dbContext.CategoryKeywords
            .AsNoTracking()
            .Select(keyword => new CategoryKeywordEntry(
                keyword.CategoryId, keyword.Category.Name, keyword.Category.DisplayOrder, keyword.Keyword))
            .ToListAsync(cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _dbContext.SaveChangesAsync(cancellationToken);

    // Shared by the paged queue query and the tab-count query — search matches listing title or
    // owner first/last/full name/email, case-insensitive (SQL Server's default collation is CI;
    // see SqliteTestDatabase for how tests get the same behaviour against SQLite).
    private static IQueryable<Listing> ApplySearch(IQueryable<Listing> query, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return query;
        }

        var term = search.Trim();
        return query.Where(listing =>
            listing.Title.Contains(term) ||
            listing.Owner.FirstName.Contains(term) ||
            listing.Owner.LastName.Contains(term) ||
            (listing.Owner.FirstName + " " + listing.Owner.LastName).Contains(term) ||
            listing.Owner.Email.Contains(term));
    }
}
