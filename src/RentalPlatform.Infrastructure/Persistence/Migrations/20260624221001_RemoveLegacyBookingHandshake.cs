using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLegacyBookingHandshake : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompletedVia",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "ReturnInitiatedBy",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "ReturnMarkedAt",
                table: "Bookings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CompletedVia",
                table: "Bookings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReturnInitiatedBy",
                table: "Bookings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReturnMarkedAt",
                table: "Bookings",
                type: "datetime2",
                nullable: true);
        }
    }
}
