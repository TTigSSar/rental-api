using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Application.Services;
using RentalPlatform.Infrastructure.DependencyInjection.DevelopmentSeed;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// P3-3: the dev seed counts its bookings in the demand table (same SaveChanges), and a second run, which
// skips the already-present bookings, does not count them again.
public sealed class DevelopmentSeedDemandCountsTests
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
    public async Task The_Seed_Counts_Every_Seeded_Booking_Once_Even_When_Run_Twice()
    {
        using var db = new SqliteTestDatabase();

        for (var run = 0; run < 2; run++)
        {
            await using var context = db.CreateContext();
            await CreateRunner(context).RunAsync(CancellationToken.None);
        }

        await using var verify = db.CreateContext();
        var bookings = await verify.Bookings.CountAsync();
        var counted = await verify.DemandDailyCounts.SumAsync(count => count.BookingRequests);
        Assert.True(bookings > 0);
        Assert.Equal(bookings, counted);
    }
}
