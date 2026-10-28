using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChatMessageMeetingPoint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MeetingPointKind",
                table: "ChatMessages",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MeetingPointLatitude",
                table: "ChatMessages",
                type: "decimal(9,6)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MeetingPointLongitude",
                table: "ChatMessages",
                type: "decimal(9,6)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MeetingPointKind",
                table: "ChatMessages");

            migrationBuilder.DropColumn(
                name: "MeetingPointLatitude",
                table: "ChatMessages");

            migrationBuilder.DropColumn(
                name: "MeetingPointLongitude",
                table: "ChatMessages");
        }
    }
}
