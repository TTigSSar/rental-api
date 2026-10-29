using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiSearchLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiSearchLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ParseModel = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RankModel = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    InputTokens = table.Column<int>(type: "int", nullable: false),
                    OutputTokens = table.Column<int>(type: "int", nullable: false),
                    CostUsd = table.Column<decimal>(type: "decimal(10,6)", nullable: false),
                    LatencyMs = table.Column<int>(type: "int", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiSearchLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiSearchLogs_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiSearchLogs_CreatedAt",
                table: "AiSearchLogs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AiSearchLogs_UserId",
                table: "AiSearchLogs",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiSearchLogs");
        }
    }
}
