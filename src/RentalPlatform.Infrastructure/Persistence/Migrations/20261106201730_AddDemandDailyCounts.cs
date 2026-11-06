using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDemandDailyCounts : Migration
    {
        /// <summary>
        /// The backfill, reachable from tests (same pattern as AddUserHomePoint.DataSql). Counts every
        /// existing booking (all statuses: each row was a persisted request) on its Yerevan day, under
        /// the listing's CURRENT district and category. Yerevan is a fixed UTC+4 (ADR-048), so the day is
        /// CAST(DATEADD(HOUR, 4, CreatedAt) AS date); YerevanCalendar.DateOf is the same arithmetic in C#.
        /// </summary>
        internal static class DataSql
        {
            internal const string BackfillFromBookings =
                """
                INSERT INTO [DemandDailyCounts] ([Id], [Day], [DistrictId], [CategoryId], [BookingRequests])
                SELECT NEWID(), CAST(DATEADD(HOUR, 4, b.[CreatedAt]) AS date), l.[DistrictId], l.[CategoryId], COUNT(*)
                FROM [Bookings] AS b
                JOIN [Listings] AS l ON l.[Id] = b.[ListingId]
                GROUP BY CAST(DATEADD(HOUR, 4, b.[CreatedAt]) AS date), l.[DistrictId], l.[CategoryId];
                """;
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DemandDailyCounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    DistrictId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingRequests = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DemandDailyCounts", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DemandDailyCounts_Day_DistrictId_CategoryId",
                table: "DemandDailyCounts",
                columns: new[] { "Day", "DistrictId", "CategoryId" },
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.Sql(DataSql.BackfillFromBookings);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // IRREVERSIBLE: this drops the counters, and live counts snapshot district and category at
            // request time, so a re-backfill from Bookings cannot reproduce them after any home move,
            // reclassification or category reassign. Rollback is a backup restore (ADR-023, ADR-058),
            // never Down followed by Up.
            migrationBuilder.DropTable(
                name: "DemandDailyCounts");
        }
    }
}
