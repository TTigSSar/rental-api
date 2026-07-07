using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Category = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Meta = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Urgent = table.Column<bool>(type: "bit", nullable: false),
                    ActorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ActorAvatarUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    ActorVerified = table.Column<bool>(type: "bit", nullable: false),
                    ActorIsSystem = table.Column<bool>(type: "bit", nullable: false),
                    ActorSystemIcon = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    EntityType = table.Column<int>(type: "int", nullable: false),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeepLink = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    ToyTitle = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ToyImageUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    PrimaryActionLabel = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    PrimaryActionDeepLink = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    SecondaryActionLabel = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    SecondaryActionDeepLink = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    ReadAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notifications_Users_RecipientId",
                        column: x => x.RecipientId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_RecipientId_ReadAt_CreatedAt",
                table: "Notifications",
                columns: new[] { "RecipientId", "ReadAt", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Notifications");
        }
    }
}
