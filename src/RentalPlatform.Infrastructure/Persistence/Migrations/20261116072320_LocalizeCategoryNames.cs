using System.Linq;
using Microsoft.EntityFrameworkCore.Migrations;
using RentalPlatform.Infrastructure.DependencyInjection.DevelopmentSeed;

#nullable disable

namespace RentalPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// ADR-062: category names are data, served in all three languages (Name = English, NameHy,
    /// NameRu). Schema + data in one migration, sibling of 20260814080938_BackfillCategoryIconAndColor
    /// (read its header first — same "reference data ships via migration, not just the
    /// Development-only seed" reasoning).
    ///
    /// Steps:
    ///   1. Add [NameHy]/[NameRu] as NULLable (existing rows have no value yet).
    ///   2. UPDATE keyed on the stable Slug (ADR-016) for the 10 reference categories, setting the
    ///      Armenian/Russian name only where the column is still NULL. The values are read from
    ///      DevelopmentSeedData.Categories (same assembly) so this migration and the Development
    ///      seed cannot drift apart. Re-running is a no-op (the NULL guard).
    ///   3. Every remaining NULL row (admin-created categories, or reference slugs that were
    ///      deleted/renamed) falls back to the English [Name] in both columns, so no row is left
    ///      with a blank name. An admin can then edit them; DevelopmentSeedRunner only replaces a
    ///      value that is blank or still equals [Name].
    ///   4. Alter both columns to NOT NULL, nvarchar(120).
    /// Down drops both columns (the localized text is lost; English Name is untouched).
    /// </remarks>
    public partial class LocalizeCategoryNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NameHy",
                table: "Categories",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NameRu",
                table: "Categories",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);

            var values = string.Join(",\n    ", DevelopmentSeedData.Categories.Select(category =>
                $"(N'{category.Slug}', N'{category.NameHy.Replace("'", "''")}', N'{category.NameRu.Replace("'", "''")}')"));

            migrationBuilder.Sql($@"
MERGE INTO [Categories] AS target
USING (VALUES
    {values}
) AS source ([Slug], [NameHy], [NameRu])
ON target.[Slug] = source.[Slug]
WHEN MATCHED THEN UPDATE SET
    target.[NameHy] = CASE WHEN target.[NameHy] IS NULL THEN source.[NameHy] ELSE target.[NameHy] END,
    target.[NameRu] = CASE WHEN target.[NameRu] IS NULL THEN source.[NameRu] ELSE target.[NameRu] END;
");

            migrationBuilder.Sql(@"
UPDATE [Categories] SET [NameHy] = [Name] WHERE [NameHy] IS NULL;
UPDATE [Categories] SET [NameRu] = [Name] WHERE [NameRu] IS NULL;
");

            migrationBuilder.AlterColumn<string>(
                name: "NameHy",
                table: "Categories",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(120)",
                oldMaxLength: 120,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "NameRu",
                table: "Categories",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(120)",
                oldMaxLength: 120,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NameHy",
                table: "Categories");

            migrationBuilder.DropColumn(
                name: "NameRu",
                table: "Categories");
        }
    }
}
