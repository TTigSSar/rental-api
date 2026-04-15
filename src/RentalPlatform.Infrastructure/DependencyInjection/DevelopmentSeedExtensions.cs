using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Infrastructure.Persistence;

namespace RentalPlatform.Infrastructure.DependencyInjection;

public static class DevelopmentSeedExtensions
{
    private sealed record SeedCategory(Guid Id, string Name, string Slug);

    private static readonly SeedCategory[] DefaultCategories =
    [
        new(new Guid("f450befb-b2af-4f1e-8f34-0f9fd70d9c96"), "Apartments", "apartments"),
        new(new Guid("d1f1e1d9-3a9d-4cde-9a7a-213844e0f4d8"), "Houses", "houses"),
        new(new Guid("cb2f147c-95f2-4f69-b065-c01ee3464f3a"), "Cars", "cars"),
        new(new Guid("8f4b38ff-2e27-4f23-957c-2ea1a2f3554a"), "Electronics", "electronics"),
        new(new Guid("d41f8b63-e019-4f8e-8bc6-0ab90d6f8d0f"), "Toys", "toys"),
        new(new Guid("f91a1f36-2063-4a7f-b4b1-65f30c6f6ef5"), "Tools", "tools")
    ];

    public static async Task SeedDevelopmentDataAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DevelopmentSeed");
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var targetSlugs = DefaultCategories.Select(category => category.Slug).ToArray();
        var existingSlugs = await dbContext.Categories
            .Where(category => targetSlugs.Contains(category.Slug))
            .Select(category => category.Slug)
            .ToListAsync(cancellationToken);

        var missingCategories = DefaultCategories
            .Where(seedCategory => !existingSlugs.Contains(seedCategory.Slug, StringComparer.OrdinalIgnoreCase))
            .Select(seedCategory => new Category
            {
                Id = seedCategory.Id,
                Name = seedCategory.Name,
                Slug = seedCategory.Slug
            })
            .ToArray();

        if (missingCategories.Length == 0)
        {
            logger.LogInformation("Development seed skipped. Categories already present.");
            return;
        }

        await dbContext.Categories.AddRangeAsync(missingCategories, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Development seed completed. Inserted {Count} categories.", missingCategories.Length);
    }
}
