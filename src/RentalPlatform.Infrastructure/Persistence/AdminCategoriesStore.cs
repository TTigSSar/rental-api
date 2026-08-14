using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Infrastructure.Persistence;

public sealed class AdminCategoriesStore : IAdminCategoriesStore
{
    private readonly AppDbContext _dbContext;

    public AdminCategoriesStore(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _dbContext.Users.FirstOrDefaultAsync(user => user.Id == userId, cancellationToken);

    public async Task<IReadOnlyCollection<Category>> GetAllOrderedAsync(CancellationToken cancellationToken = default) =>
        await _dbContext.Categories
            .AsNoTracking()
            .OrderBy(category => category.DisplayOrder)
            .ThenBy(category => category.Name)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, int>> GetApprovedListingCountsAsync(
        CancellationToken cancellationToken = default)
    {
        var counts = await _dbContext.Listings
            .AsNoTracking()
            .Where(listing => listing.Status == ListingStatus.Approved)
            .GroupBy(listing => listing.CategoryId)
            .Select(g => new { CategoryId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return counts.ToDictionary(x => x.CategoryId, x => x.Count);
    }

    public Task<Category?> FindByIdAsync(Guid categoryId, CancellationToken cancellationToken = default) =>
        _dbContext.Categories.FirstOrDefaultAsync(category => category.Id == categoryId, cancellationToken);

    // Client-evaluated on purpose: SQL Server's default collation is case-insensitive so `==`
    // there already behaves this way, but SQLite (the test provider — see SqliteTestDatabase) is
    // byte-exact on `==` with no equivalent override to the Contains/instr() shim. The Categories
    // table is small (tens of rows, a fixed reference catalog), so loading Name/Slug and comparing
    // with StringComparison.OrdinalIgnoreCase keeps behaviour identical across both providers
    // instead of depending on collation.
    public async Task<bool> NameOrSlugExistsAsync(
        string name, string slug, Guid? excludeCategoryId, CancellationToken cancellationToken = default)
    {
        var candidates = await _dbContext.Categories
            .AsNoTracking()
            .Where(category => excludeCategoryId == null || category.Id != excludeCategoryId.Value)
            .Select(category => new { category.Name, category.Slug })
            .ToListAsync(cancellationToken);

        return candidates.Any(c =>
            string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.Slug, slug, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<int> GetMaxDisplayOrderAsync(CancellationToken cancellationToken = default)
    {
        var hasAny = await _dbContext.Categories.AnyAsync(cancellationToken);
        return hasAny ? await _dbContext.Categories.MaxAsync(category => category.DisplayOrder, cancellationToken) : 0;
    }

    public Task<int> GetListingCountAnyStatusAsync(Guid categoryId, CancellationToken cancellationToken = default) =>
        _dbContext.Listings.CountAsync(listing => listing.CategoryId == categoryId, cancellationToken);

    public async Task AddAsync(Category category, CancellationToken cancellationToken = default) =>
        await _dbContext.Categories.AddAsync(category, cancellationToken);

    public async Task ReorderAsync(IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var categories = await _dbContext.Categories
            .Where(category => orderedIds.Contains(category.Id))
            .ToListAsync(cancellationToken);

        var categoryById = categories.ToDictionary(category => category.Id);
        for (var position = 0; position < orderedIds.Count; position++)
        {
            categoryById[orderedIds[position]].DisplayOrder = position;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteAsync(Category category, CancellationToken cancellationToken = default)
    {
        _dbContext.Categories.Remove(category);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteWithReassignAsync(
        Guid sourceCategoryId, Guid targetCategoryId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Bulk move first — every listing of every status, not just Approved — so the category is
        // never left dangling with rows still pointing at it (Listing.CategoryId is required, FK
        // is Restrict, so deleting the category first would fail/orphan anyway; this ordering is
        // the only one that keeps "a listing is never without a category" true at every instant).
        await _dbContext.Listings
            .Where(listing => listing.CategoryId == sourceCategoryId)
            .ExecuteUpdateAsync(update => update.SetProperty(listing => listing.CategoryId, targetCategoryId), cancellationToken);

        var category = await _dbContext.Categories.FirstAsync(c => c.Id == sourceCategoryId, cancellationToken);
        _dbContext.Categories.Remove(category);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _dbContext.SaveChangesAsync(cancellationToken);
}
