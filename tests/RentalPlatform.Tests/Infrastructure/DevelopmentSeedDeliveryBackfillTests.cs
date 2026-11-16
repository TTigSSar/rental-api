using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.DependencyInjection.DevelopmentSeed;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// Seeded listings carry a handover option and a minimum rental; the backfill fills only null/None
// values on existing rows and never breaks a seeded booking (ADR-043).
public sealed class DevelopmentSeedDeliveryBackfillTests
{
    private sealed class NeverSucceedsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static DevelopmentSeedRunner CreateRunner(AppDbContext context) => new(
        context,
        new BcryptPasswordHasher(),
        NullLogger<DevelopmentSeedRunner>.Instance,
        new FakeFileStorageService(),
        new HttpClient(new NeverSucceedsHandler()),
        new HomePointService(
            new HomePointStore(context),
            new GeohashSnapper(),
            new DistrictBoundaryProvider(),
            new FakeNotificationEmitter()));

    [Fact]
    public async Task Seed_Fills_Delivery_And_MinRental_Without_Breaking_Seeded_Bookings_Or_Overwriting_Edits()
    {
        using var db = new SqliteTestDatabase();
        await using (var context = db.CreateContext())
        {
            await CreateRunner(context).RunAsync(CancellationToken.None);
        }

        var seedIds = DevelopmentSeedData.Listings.Select(l => l.Id).ToArray();
        var editedId = seedIds[0];
        await using (var context = db.CreateContext())
        {
            var listings = await context.Listings.Where(l => seedIds.Contains(l.Id)).ToListAsync();
            Assert.All(listings, l =>
            {
                Assert.NotNull(l.MinRentalDays);
                Assert.NotEqual(DeliveryOptions.None, l.DeliveryOptions);
                Assert.Equal(DeliveryOptionsMapper.ToLegacy(l.DeliveryOptions), l.DeliveryType);
            });

            foreach (var booking in await context.Bookings.ToListAsync())
            {
                var listing = listings.First(l => l.Id == booking.ListingId);
                var days = Math.Max(1, booking.EndDate.DayNumber - booking.StartDate.DayNumber);
                Assert.True(days >= listing.MinRentalDays);
            }

            // Simulate an older dev row (nulls) and a hand-edited one.
            var other = listings.First(l => l.Id != editedId);
            other.MinRentalDays = null;
            other.DeliveryOptions = null;
            other.DeliveryType = null;
            var edited = listings.First(l => l.Id == editedId);
            edited.MinRentalDays = 5;
            edited.DeliveryOptions = DeliveryOptions.Courier;
            edited.DeliveryType = DeliveryType.Courier;
            await context.SaveChangesAsync();

            await CreateRunner(context).RunAsync(CancellationToken.None);
        }

        await using var verify = db.CreateContext();
        var edit = await verify.Listings.SingleAsync(l => l.Id == editedId);
        Assert.Equal(5, edit.MinRentalDays);
        Assert.Equal(DeliveryOptions.Courier, edit.DeliveryOptions);
        Assert.DoesNotContain(await verify.Listings.Where(l => seedIds.Contains(l.Id)).ToListAsync(),
            l => l.MinRentalDays is null || l.DeliveryOptions is null);
    }
}
