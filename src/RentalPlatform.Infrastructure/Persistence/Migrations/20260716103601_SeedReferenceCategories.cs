using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Categories are reference data the marketplace needs in every environment, not demo data —
    /// so unlike Listings/Users/etc. they now ship via migration instead of only via the
    /// Development-only seed (which never runs in Production).
    ///
    /// Deliberately NOT modeled with EF Core's HasData/seed-data mechanism: HasData emits a bare
    /// INSERT per row and would throw a primary-key violation on every database that already has
    /// these exact rows (fixed GUIDs) from the Development seed — Tigran's local dev DB, the Docker
    /// dev stack, and the qa-engineer real-stack e2e environment all already contain them. Instead
    /// this uses an explicit, idempotent MERGE keyed on Id: existing rows are left completely
    /// untouched (matches the Development seed's own idempotent-by-Id behavior), and only missing
    /// rows are inserted — safe to apply against both a database that already has this data and a
    /// fresh empty one.
    ///
    /// The Development seed's own category-seeding code (DevelopmentSeedRunner.SeedCategoriesAsync)
    /// is left in place unchanged: it is already idempotent-by-slug, so after this migration runs it
    /// simply finds all ten categories present and becomes a no-op on every subsequent Development
    /// startup — no special-casing needed.
    /// </remarks>
    public partial class SeedReferenceCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
MERGE INTO [Categories] AS target
USING (VALUES
    ('c0000004-0000-4000-9000-000000000004', N'Baby Toys',        N'baby-toys',        N'pi-heart',    N'/assets/categories/baby-toys.svg',        1),
    ('c0000002-0000-4000-9000-000000000002', N'Building Blocks',  N'building-blocks',  N'pi-box',      N'/assets/categories/building-blocks.svg',  2),
    ('c0000001-0000-4000-9000-000000000001', N'Educational Toys', N'educational-toys', N'pi-book',     N'/assets/categories/educational-toys.svg', 3),
    ('c0000003-0000-4000-9000-000000000003', N'Outdoor Toys',     N'outdoor-toys',     N'pi-sun',      N'/assets/categories/outdoor-toys.svg',     4),
    ('c0000007-0000-4000-9000-000000000007', N'Ride-On Toys',     N'ride-on-toys',     N'pi-car',      N'/assets/categories/ride-on-toys.svg',     5),
    ('c0000006-0000-4000-9000-000000000006', N'Pretend Play',     N'pretend-play',     N'pi-palette',  N'/assets/categories/pretend-play.svg',     6),
    ('c0000009-0000-4000-9000-000000000009', N'Montessori Toys',  N'montessori-toys',  N'pi-leaf',     N'/assets/categories/montessori-toys.svg',  7),
    ('c0000008-0000-4000-9000-000000000008', N'Puzzles',          N'puzzles',          N'pi-th-large', N'/assets/categories/puzzles.svg',          8),
    ('c0000005-0000-4000-9000-000000000005', N'Board Games',      N'board-games',      N'pi-table',    N'/assets/categories/board-games.svg',      9),
    ('c000000a-0000-4000-9000-00000000000a', N'Party Toys',       N'party-toys',       N'pi-gift',     N'/assets/categories/party-toys.svg',       10)
) AS source ([Id], [Name], [Slug], [IconName], [ImageUrl], [DisplayOrder])
ON target.[Id] = source.[Id]
WHEN NOT MATCHED BY TARGET THEN
    INSERT ([Id], [Name], [Slug], [IconName], [ImageUrl], [DisplayOrder])
    VALUES (source.[Id], source.[Name], source.[Slug], source.[IconName], source.[ImageUrl], source.[DisplayOrder]);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only remove the rows this migration added, and only where nothing now references
            // them — a category with listings against it (virtually guaranteed on any database
            // that has been running for a while) is left in place rather than throwing an FK
            // violation or silently orphaning listings.
            migrationBuilder.Sql(@"
DELETE FROM [Categories]
WHERE [Id] IN (
    'c0000004-0000-4000-9000-000000000004',
    'c0000002-0000-4000-9000-000000000002',
    'c0000001-0000-4000-9000-000000000001',
    'c0000003-0000-4000-9000-000000000003',
    'c0000007-0000-4000-9000-000000000007',
    'c0000006-0000-4000-9000-000000000006',
    'c0000009-0000-4000-9000-000000000009',
    'c0000008-0000-4000-9000-000000000008',
    'c0000005-0000-4000-9000-000000000005',
    'c000000a-0000-4000-9000-00000000000a'
)
AND NOT EXISTS (SELECT 1 FROM [Listings] WHERE [Listings].[CategoryId] = [Categories].[Id]);
");
        }
    }
}
