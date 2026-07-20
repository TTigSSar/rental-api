using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Data-only backfill: the default currency for newly authored listings flipped from USD to
    /// AMD (see ListingsOwnerService.CreateAsync) and the UI now renders every price with a fixed
    /// "֏" suffix regardless of the per-listing Currency field, but no migration shipped to convert
    /// the USD rows that predate that change. Left alone they render ~400x too cheap.
    ///
    /// Only rows with Currency = 'USD' are touched; already-AMD rows are left completely alone.
    /// Conversion uses a fixed rate of 400 AMD/USD and rounds to whole dram (AMD is not
    /// conventionally used with subunits) via ROUND(x * 400, 0) — mathematically a no-op here since
    /// both PricePerDay and DepositAmount are decimal(18,2) and 400 * (n/100) is always an integer,
    /// but kept explicit so the whole-dram rounding decision is visible in the SQL rather than
    /// implied by column precision. decimal(18,2) already accommodates the post-conversion
    /// magnitudes (300.00 USD -> 120000.00 AMD, nowhere near the ~10^16 ceiling of (18,2)), so no
    /// column widening is needed and none is done here.
    ///
    /// Bookings.TotalPrice has no Currency column of its own — it is a denormalized snapshot
    /// computed from the listing's price at booking-creation time — so any booking created against
    /// a USD listing before this migration is carrying a USD-magnitude total and must be converted
    /// too, or it goes stale/inconsistent the moment its listing flips to AMD. There are no other
    /// USD-denominated columns in the schema (no payment/transaction tables exist).
    ///
    /// Down needs to reverse *only* the rows this Up converted, not "whatever is Currency = 'AMD' at
    /// rollback time" — after Up runs, genuinely-AMD listings are indistinguishable from
    /// USD-converted ones by Currency alone, and a naive `WHERE Currency = 'AMD'` divide would
    /// corrupt every legitimate AMD listing created (or AMD booking placed) after this migration
    /// ran. To keep Down precise, Up first snapshots the affected listing IDs and their pre-
    /// conversion amounts into a small owned table, and Down restores from that snapshot by ID
    /// before dropping it. Restoration is exact (not just "divide by 400") since 400 * (n/100) is
    /// always an integer for decimal(18,2) inputs, so the forward conversion never loses
    /// information for the data this migration actually touches.
    ///
    /// Caveat (documented, not fixed here — inherent to any post-hoc rollback): if a converted
    /// listing is edited (new PricePerDay/DepositAmount) or a new booking is placed against it
    /// between Up and a later Down, Down has no "before" value for that new data and will overwrite
    /// it with the stale snapshot / incorrectly divide a genuinely-AMD booking total by 400. Down
    /// should only be run promptly after Up, before further activity on the affected listings.
    /// </remarks>
    public partial class BackfillUsdListingsToAmd : Migration
    {
        private const string SnapshotTable = "__BackfillUsdListingsToAmd_Snapshot";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($@"
CREATE TABLE [{SnapshotTable}] (
    [ListingId] uniqueidentifier NOT NULL PRIMARY KEY,
    [OriginalPricePerDay] decimal(18,2) NOT NULL,
    [OriginalDepositAmount] decimal(18,2) NULL
);

INSERT INTO [{SnapshotTable}] ([ListingId], [OriginalPricePerDay], [OriginalDepositAmount])
SELECT [Id], [PricePerDay], [DepositAmount]
FROM [Listings]
WHERE [Currency] = 'USD';

UPDATE [Listings]
SET [PricePerDay] = ROUND([PricePerDay] * 400, 0),
    [DepositAmount] = CASE WHEN [DepositAmount] IS NULL THEN NULL ELSE ROUND([DepositAmount] * 400, 0) END,
    [Currency] = 'AMD'
WHERE [Currency] = 'USD';

UPDATE [b]
SET [b].[TotalPrice] = ROUND([b].[TotalPrice] * 400, 0)
FROM [Bookings] AS [b]
INNER JOIN [{SnapshotTable}] AS [snap] ON [snap].[ListingId] = [b].[ListingId];
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($@"
UPDATE [b]
SET [b].[TotalPrice] = ROUND([b].[TotalPrice] / 400.0, 2)
FROM [Bookings] AS [b]
INNER JOIN [{SnapshotTable}] AS [snap] ON [snap].[ListingId] = [b].[ListingId];

UPDATE [l]
SET [l].[PricePerDay] = [snap].[OriginalPricePerDay],
    [l].[DepositAmount] = [snap].[OriginalDepositAmount],
    [l].[Currency] = 'USD'
FROM [Listings] AS [l]
INNER JOIN [{SnapshotTable}] AS [snap] ON [snap].[ListingId] = [l].[Id];

DROP TABLE [{SnapshotTable}];
");
        }
    }
}
