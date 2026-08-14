using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Abstractions;

public interface IAdminCategoriesStore
{
    Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>All categories, ordered by DisplayOrder (untracked).</summary>
    Task<IReadOnlyCollection<Category>> GetAllOrderedAsync(CancellationToken cancellationToken = default);

    /// <summary>Approved-listing count per category id — one grouped query, never per-row. A
    /// category with no approved listings is simply absent from the result.</summary>
    Task<IReadOnlyDictionary<Guid, int>> GetApprovedListingCountsAsync(CancellationToken cancellationToken = default);

    /// <summary>Tracked lookup, for mutation.</summary>
    Task<Category?> FindByIdAsync(Guid categoryId, CancellationToken cancellationToken = default);

    /// <summary>Case-insensitive duplicate check against every category's Name or Slug, optionally
    /// excluding one category id (used when renaming a category against itself).</summary>
    Task<bool> NameOrSlugExistsAsync(
        string name, string slug, Guid? excludeCategoryId, CancellationToken cancellationToken = default);

    Task<int> GetMaxDisplayOrderAsync(CancellationToken cancellationToken = default);

    /// <summary>Listing count of any status for the category — used by the delete decision (a
    /// category is only trivially deletable when this is zero).</summary>
    Task<int> GetListingCountAnyStatusAsync(Guid categoryId, CancellationToken cancellationToken = default);

    Task AddAsync(Category category, CancellationToken cancellationToken = default);

    /// <summary>Assigns DisplayOrder by each id's position in <paramref name="orderedIds"/>, in one
    /// transaction. Caller has already validated the id set matches exactly.</summary>
    Task ReorderAsync(IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default);

    /// <summary>Deletes a category that has zero listings.</summary>
    Task DeleteAsync(Category category, CancellationToken cancellationToken = default);

    /// <summary>Moves every listing (any status) from <paramref name="sourceCategoryId"/> to
    /// <paramref name="targetCategoryId"/>, then deletes the source category — one transaction, so a
    /// listing is never left without a category.</summary>
    Task DeleteWithReassignAsync(
        Guid sourceCategoryId, Guid targetCategoryId, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
