using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddListingDeliveryOptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DeliveryOptions",
                table: "Listings",
                type: "int",
                nullable: true);

            // Backfill: mirror each existing legacy DeliveryType value into the new flags column
            // (DeliveryType.Pickup = 0 -> DeliveryOptions.Pickup = 1; DeliveryType.Courier = 1 ->
            // DeliveryOptions.Courier = 2). Rows with no legacy value stay NULL.
            migrationBuilder.Sql(
                "UPDATE Listings SET DeliveryOptions = CASE DeliveryType WHEN 0 THEN 1 WHEN 1 THEN 2 END WHERE DeliveryType IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeliveryOptions",
                table: "Listings");
        }
    }
}
