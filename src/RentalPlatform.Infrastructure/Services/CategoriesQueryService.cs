using Microsoft.EntityFrameworkCore;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Infrastructure.Persistence;

namespace RentalPlatform.Infrastructure.Services;

public sealed class CategoriesQueryService : ICategoriesQueryService
{
    private readonly AppDbContext _dbContext;

    public CategoriesQueryService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyCollection<CategoryResponse>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.Categories
            .AsNoTracking()
            // Admin console Phase 2: a category hidden by an admin (IsVisible = false) must not
            // appear to renters browsing/filtering by category. Listings already in that category
            // stay reachable by direct link and in their owner's my-listings — this endpoint (and
            // the category filter it feeds) is the only thing gated here.
            .Where(category => category.IsVisible)
            .OrderBy(category => category.DisplayOrder)
            .ThenBy(category => category.Name)
            .Select(category => new CategoryResponse
            {
                Id = category.Id,
                Name = category.Name,
                Slug = category.Slug,
                IconName = category.IconName,
                ImageUrl = category.ImageUrl,
                ColorHex = category.ColorHex,
                DisplayOrder = category.DisplayOrder
            })
            .ToListAsync(cancellationToken);
    }
}
