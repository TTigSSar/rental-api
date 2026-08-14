using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore.Migrations;
using RentalPlatform.Infrastructure.DependencyInjection.DevelopmentSeed;

#nullable disable

namespace RentalPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Data-only backfill, sibling of 20260716103601_SeedReferenceCategories (read that migration's
    /// header first — same "reference data ships via migration, not just the Development-only seed"
    /// reasoning applies here).
    ///
    /// That migration's MERGE only ever inserted [Id], [Name], [Slug], [IconName], [ImageUrl],
    /// [DisplayOrder] — [ColorHex] was never in its column list. So on every database that only ever
    /// ran that migration (staging/production, and any dev DB older than the ColorHex column), these
    /// 10 reference categories still carry the original PrimeIcon [IconName] values (pi-heart,
    /// pi-box, ...) and [ColorHex] IS NULL. DevelopmentSeedData.Categories moved on to the shared
    /// app-icon vocabulary (heart, grid, star, ...) plus real ColorHex values, and
    /// DevelopmentSeedRunner backfills that onto Development databases on every startup - but
    /// Production never runs the Development seed, so its rows were never fixed. Once the frontend
    /// renders ColorHex + the app-icon glyph instead of ImageUrl, these rows would show as ten
    /// identical fallback-coloured tiles with the default glyph.
    ///
    /// Guarded per-column, per-row, keyed on Slug (stable per ADR-016; Name is not, since renaming a
    /// category never regenerates its slug):
    ///   - [IconName] is only overwritten where it still equals the *original* PrimeIcon value this
    ///     migration expects for that slug. If an admin has since restyled the category's icon (to
    ///     anything else, including back to a PrimeIcon name by coincidence — vanishingly unlikely
    ///     since the admin picker only offers the app-icon vocabulary), that edit is left alone.
    ///   - [ColorHex] is only overwritten where it is still NULL. Any admin-set colour, including one
    ///     that happens to match a palette swatch, is left alone.
    /// The two guards are independent per row (a MERGE ... WHEN MATCHED with a CASE per column,
    /// rather than plain INSERT-avoidance MERGE like the sibling migration needs for a fresh vs.
    /// existing PK) — e.g. a row with a customised icon but still-NULL colour gets only its colour
    /// filled in. Re-running this migration against a database it has already fixed is a no-op: both
    /// CASE conditions evaluate false and every SET is a self-assignment.
    ///
    /// The target [IconName]/[ColorHex] values are read directly from
    /// DevelopmentSeedData.Categories (same assembly, internal type) rather than retyped as SQL
    /// literals, so this migration and the Development seed cannot drift apart. The *original*
    /// PrimeIcon values are NOT sourced from anywhere live — DevelopmentSeedData no longer has them
    /// once it moves to the new vocabulary — so they are frozen here as the historical constants
    /// they are: exactly what 20260716103601_SeedReferenceCategories.cs inserted, and that migration
    /// is never edited after being applied.
    /// </remarks>
    public partial class BackfillCategoryIconAndColor : Migration
    {
        // Exactly what 20260716103601_SeedReferenceCategories.cs inserted for [IconName], keyed by
        // the category's stable Slug. This is a snapshot of history, not a live value — it must stay
        // exactly as written even if the seed's vocabulary changes again later; it is the guard for
        // "this row has never been touched since that original migration ran", not a target value.
        private static readonly IReadOnlyDictionary<string, string> OriginalPrimeIconNameBySlug =
            new Dictionary<string, string>
            {
                ["baby-toys"] = "pi-heart",
                ["building-blocks"] = "pi-box",
                ["educational-toys"] = "pi-book",
                ["outdoor-toys"] = "pi-sun",
                ["ride-on-toys"] = "pi-car",
                ["pretend-play"] = "pi-palette",
                ["montessori-toys"] = "pi-leaf",
                ["puzzles"] = "pi-th-large",
                ["board-games"] = "pi-table",
                ["party-toys"] = "pi-gift",
            };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var rows = DevelopmentSeedData.Categories
                .Where(category => OriginalPrimeIconNameBySlug.ContainsKey(category.Slug))
                .Select(category => (
                    Slug: category.Slug,
                    OldIconName: OriginalPrimeIconNameBySlug[category.Slug],
                    NewIconName: category.IconName,
                    NewColorHex: category.ColorHex))
                .ToArray();

            var values = string.Join(",\n    ", rows.Select(row =>
                $"(N'{row.Slug}', N'{row.OldIconName}', N'{row.NewIconName}', N'{row.NewColorHex}')"));

            migrationBuilder.Sql($@"
MERGE INTO [Categories] AS target
USING (VALUES
    {values}
) AS source ([Slug], [OldIconName], [NewIconName], [NewColorHex])
ON target.[Slug] = source.[Slug]
WHEN MATCHED THEN UPDATE SET
    target.[IconName] = CASE
        WHEN target.[IconName] = source.[OldIconName] THEN source.[NewIconName]
        ELSE target.[IconName]
    END,
    target.[ColorHex] = CASE
        WHEN target.[ColorHex] IS NULL THEN source.[NewColorHex]
        ELSE target.[ColorHex]
    END;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally a no-op: the only meaningful revert would restore the pre-migration
            // PrimeIcon [IconName] values and null out [ColorHex] again, which just re-introduces
            // the ten-identical-fallback-tiles bug this migration exists to fix. There is no
            // scenario where rolling back to a known-broken glyph/colour state is the desired
            // outcome, so nothing is done here rather than faking a revert with no value. (Rows an
            // admin already customised were never touched by Up, so there is nothing of theirs to
            // restore either.)
        }
    }
}
