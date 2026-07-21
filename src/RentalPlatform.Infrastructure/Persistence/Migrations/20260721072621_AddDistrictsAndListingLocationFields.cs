using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace RentalPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDistrictsAndListingLocationFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DistrictId",
                table: "Listings",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LocationKind",
                table: "Listings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "PublicLatitude",
                table: "Listings",
                type: "decimal(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PublicLongitude",
                table: "Listings",
                type: "decimal(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Districts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    NameHy = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    NameRu = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Districts", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "Districts",
                columns: new[] { "Id", "Code", "NameEn", "NameHy", "NameRu" },
                values: new object[,]
                {
                    { new Guid("d0000001-0000-4000-9000-000000000001"), "ajapnyak", "Ajapnyak", "Աջափնյակ", "Ачапняк" },
                    { new Guid("d0000002-0000-4000-9000-000000000002"), "arabkir", "Arabkir", "Արաբկիր", "Арабкир" },
                    { new Guid("d0000003-0000-4000-9000-000000000003"), "avan", "Avan", "Ավան", "Аван" },
                    { new Guid("d0000004-0000-4000-9000-000000000004"), "davtashen", "Davtashen", "Դավթաշեն", "Давташен" },
                    { new Guid("d0000005-0000-4000-9000-000000000005"), "erebuni", "Erebuni", "Էրեբունի", "Эребуни" },
                    { new Guid("d0000006-0000-4000-9000-000000000006"), "kanaker-zeytun", "Kanaker-Zeytun", "Քանաքեռ-Զեյթուն", "Канакер-Зейтун" },
                    { new Guid("d0000007-0000-4000-9000-000000000007"), "kentron", "Kentron", "Կենտրոն", "Кентрон" },
                    { new Guid("d0000008-0000-4000-9000-000000000008"), "malatia-sebastia", "Malatia-Sebastia", "Մալաթիա-Սեբաստիա", "Малатия-Себастия" },
                    { new Guid("d0000009-0000-4000-9000-000000000009"), "nork-marash", "Nork-Marash", "Նորք Մարաշ", "Норк Мараш" },
                    { new Guid("d000000a-0000-4000-9000-00000000000a"), "nor-nork", "Nor Nork", "Նոր Նորք", "Нор Норк" },
                    { new Guid("d000000b-0000-4000-9000-00000000000b"), "nubarashen", "Nubarashen", "Նուբարաշեն", "Нубарашен" },
                    { new Guid("d000000c-0000-4000-9000-00000000000c"), "shengavit", "Shengavit", "Շենգավիթ", "Шенгавит" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Listings_DistrictId",
                table: "Listings",
                column: "DistrictId");

            migrationBuilder.CreateIndex(
                name: "IX_Listings_PublicLatitude_PublicLongitude",
                table: "Listings",
                columns: new[] { "PublicLatitude", "PublicLongitude" });

            migrationBuilder.CreateIndex(
                name: "IX_Districts_Code",
                table: "Districts",
                column: "Code",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Listings_Districts_DistrictId",
                table: "Listings",
                column: "DistrictId",
                principalTable: "Districts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Listings_Districts_DistrictId",
                table: "Listings");

            migrationBuilder.DropTable(
                name: "Districts");

            migrationBuilder.DropIndex(
                name: "IX_Listings_DistrictId",
                table: "Listings");

            migrationBuilder.DropIndex(
                name: "IX_Listings_PublicLatitude_PublicLongitude",
                table: "Listings");

            migrationBuilder.DropColumn(
                name: "DistrictId",
                table: "Listings");

            migrationBuilder.DropColumn(
                name: "LocationKind",
                table: "Listings");

            migrationBuilder.DropColumn(
                name: "PublicLatitude",
                table: "Listings");

            migrationBuilder.DropColumn(
                name: "PublicLongitude",
                table: "Listings");
        }
    }
}
