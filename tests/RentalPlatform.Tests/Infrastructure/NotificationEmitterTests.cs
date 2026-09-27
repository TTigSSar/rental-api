using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// DoRent is AMD-only. NotificationEmitter.BuildMeta must render the booking's total price with
// the same grouping + NBSP + ֏ contract as the Angular DramCurrencyPipe (see BuildMeta's doc
// comment). This locks down the format the USD->AMD migration missed: Meta previously leaked
// the raw currency code ("7500 AMD") with no thousands separator and no non-breaking space.
public sealed class NotificationEmitterTests
{
    private static readonly Guid OwnerId = new("e6000000-0000-0000-0000-000000000001");
    private static readonly Guid RenterId = new("e6000000-0000-0000-0000-000000000002");
    private static readonly Guid CategoryId = new("e6000000-0000-0000-0000-000000000003");
    private static readonly Guid ListingId = new("e6000000-0000-0000-0000-000000000004");

    [Fact]
    public async Task BookingRequestedAsync_Meta_Groups_Amount_And_Uses_Dram_Symbol()
    {
        using var db = new SqliteTestDatabase();
        // Only the recipient (listing owner) needs to exist for the Notification FK; the booking
        // and listing themselves are never persisted by NotificationEmitter.
        await db.SeedAsync(TestData.User(OwnerId, "owner@test.local"));

        var listing = TestData.Listing(ListingId, OwnerId, CategoryId);
        var renter = TestData.User(RenterId, "renter@test.local");
        var booking = TestData.Booking(
            Guid.NewGuid(),
            ListingId,
            RenterId,
            TestData.Today,
            TestData.Today.AddDays(2), // start..start+2 inclusive == 3 days
            BookingStatus.Pending);
        booking.TotalPrice = 7500m;

        await using var context = db.CreateContext();
        var emitter = new NotificationEmitter(new NotificationsStore(context), NullLogger<NotificationEmitter>.Instance);

        await emitter.BookingRequestedAsync(booking, renter, listing);

        var notification = Assert.Single(context.Notifications);
        Assert.Equal("3 days · 7,500\u00A0֏", notification.Meta);
    }

    [Fact]
    public async Task BookingRequestedAsync_Meta_Uses_Singular_Day_Label_For_One_Day_Booking()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(TestData.User(OwnerId, "owner@test.local"));

        var listing = TestData.Listing(ListingId, OwnerId, CategoryId);
        var renter = TestData.User(RenterId, "renter@test.local");
        var booking = TestData.Booking(
            Guid.NewGuid(),
            ListingId,
            RenterId,
            TestData.Today,
            TestData.Today,
            BookingStatus.Pending);
        booking.TotalPrice = 2500m;

        await using var context = db.CreateContext();
        var emitter = new NotificationEmitter(new NotificationsStore(context), NullLogger<NotificationEmitter>.Instance);

        await emitter.BookingRequestedAsync(booking, renter, listing);

        var notification = Assert.Single(context.Notifications);
        Assert.Equal("1 day · 2,500\u00A0֏", notification.Meta);
    }
}
