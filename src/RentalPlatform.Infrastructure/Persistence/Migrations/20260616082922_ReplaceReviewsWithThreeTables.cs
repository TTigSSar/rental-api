using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceReviewsWithThreeTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Reviews");

            migrationBuilder.CreateTable(
                name: "OwnerReviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReviewerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommunicationRating = table.Column<int>(type: "int", nullable: false),
                    PickupHandoverRating = table.Column<int>(type: "int", nullable: false),
                    FriendlinessRating = table.Column<int>(type: "int", nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerReviews", x => x.Id);
                    table.CheckConstraint("CK_OwnerReviews_Communication", "[CommunicationRating] >= 1 AND [CommunicationRating] <= 5");
                    table.CheckConstraint("CK_OwnerReviews_Friendliness", "[FriendlinessRating] >= 1 AND [FriendlinessRating] <= 5");
                    table.CheckConstraint("CK_OwnerReviews_Pickup", "[PickupHandoverRating] >= 1 AND [PickupHandoverRating] <= 5");
                    table.ForeignKey(
                        name: "FK_OwnerReviews_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerReviews_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerReviews_Users_ReviewerId",
                        column: x => x.ReviewerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RenterReviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RenterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReviewerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommunicationRating = table.Column<int>(type: "int", nullable: false),
                    ReturnedOnTimeRating = table.Column<int>(type: "int", nullable: false),
                    CareOfToyRating = table.Column<int>(type: "int", nullable: false),
                    WouldRentAgainRating = table.Column<int>(type: "int", nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RenterReviews", x => x.Id);
                    table.CheckConstraint("CK_RenterReviews_Care", "[CareOfToyRating] >= 1 AND [CareOfToyRating] <= 5");
                    table.CheckConstraint("CK_RenterReviews_Communication", "[CommunicationRating] >= 1 AND [CommunicationRating] <= 5");
                    table.CheckConstraint("CK_RenterReviews_Returned", "[ReturnedOnTimeRating] >= 1 AND [ReturnedOnTimeRating] <= 5");
                    table.CheckConstraint("CK_RenterReviews_WouldRent", "[WouldRentAgainRating] >= 1 AND [WouldRentAgainRating] <= 5");
                    table.ForeignKey(
                        name: "FK_RenterReviews_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RenterReviews_Users_RenterId",
                        column: x => x.RenterId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RenterReviews_Users_ReviewerId",
                        column: x => x.ReviewerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ToyReviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ListingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReviewerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OverallRating = table.Column<int>(type: "int", nullable: false),
                    ConditionRating = table.Column<int>(type: "int", nullable: false),
                    CleanlinessRating = table.Column<int>(type: "int", nullable: false),
                    ValueForMoneyRating = table.Column<int>(type: "int", nullable: false),
                    FunPlayValueRating = table.Column<int>(type: "int", nullable: false),
                    DescriptionAccuracyRating = table.Column<int>(type: "int", nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToyReviews", x => x.Id);
                    table.CheckConstraint("CK_ToyReviews_Cleanliness", "[CleanlinessRating] >= 1 AND [CleanlinessRating] <= 5");
                    table.CheckConstraint("CK_ToyReviews_Condition", "[ConditionRating] >= 1 AND [ConditionRating] <= 5");
                    table.CheckConstraint("CK_ToyReviews_Description", "[DescriptionAccuracyRating] >= 1 AND [DescriptionAccuracyRating] <= 5");
                    table.CheckConstraint("CK_ToyReviews_Fun", "[FunPlayValueRating] >= 1 AND [FunPlayValueRating] <= 5");
                    table.CheckConstraint("CK_ToyReviews_Overall", "[OverallRating] >= 1 AND [OverallRating] <= 5");
                    table.CheckConstraint("CK_ToyReviews_Value", "[ValueForMoneyRating] >= 1 AND [ValueForMoneyRating] <= 5");
                    table.ForeignKey(
                        name: "FK_ToyReviews_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ToyReviews_Listings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "Listings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ToyReviews_Users_ReviewerId",
                        column: x => x.ReviewerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerReviews_BookingId",
                table: "OwnerReviews",
                column: "BookingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OwnerReviews_OwnerId",
                table: "OwnerReviews",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerReviews_ReviewerId",
                table: "OwnerReviews",
                column: "ReviewerId");

            migrationBuilder.CreateIndex(
                name: "IX_RenterReviews_BookingId",
                table: "RenterReviews",
                column: "BookingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RenterReviews_RenterId",
                table: "RenterReviews",
                column: "RenterId");

            migrationBuilder.CreateIndex(
                name: "IX_RenterReviews_ReviewerId",
                table: "RenterReviews",
                column: "ReviewerId");

            migrationBuilder.CreateIndex(
                name: "IX_ToyReviews_BookingId",
                table: "ToyReviews",
                column: "BookingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ToyReviews_ListingId",
                table: "ToyReviews",
                column: "ListingId");

            migrationBuilder.CreateIndex(
                name: "IX_ToyReviews_ReviewerId",
                table: "ToyReviews",
                column: "ReviewerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OwnerReviews");

            migrationBuilder.DropTable(
                name: "RenterReviews");

            migrationBuilder.DropTable(
                name: "ToyReviews");

            migrationBuilder.CreateTable(
                name: "Reviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ListingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevieweeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReviewerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Rating = table.Column<int>(type: "int", nullable: false),
                    ReviewerRole = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reviews", x => x.Id);
                    table.CheckConstraint("CK_Reviews_Rating", "[Rating] >= 1 AND [Rating] <= 5");
                    table.ForeignKey(
                        name: "FK_Reviews_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Reviews_Listings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "Listings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Reviews_Users_RevieweeId",
                        column: x => x.RevieweeId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Reviews_Users_ReviewerId",
                        column: x => x.ReviewerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_BookingId_ReviewerRole",
                table: "Reviews",
                columns: new[] { "BookingId", "ReviewerRole" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_ListingId",
                table: "Reviews",
                column: "ListingId");

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_RevieweeId",
                table: "Reviews",
                column: "RevieweeId");

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_ReviewerId",
                table: "Reviews",
                column: "ReviewerId");
        }
    }
}
