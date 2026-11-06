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

// Account deletion (ADR-056) rewrites a user's email to deleted-{id}@deleted.invalid. The dev seed
// must still recognise a deleted seed user (by its fixed Id) and leave it alone, instead of
// re-inserting the same Id and crashing the API on startup with a PK violation.
public sealed class DevelopmentSeedDeletedUserTests
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
    public async Task RunAsync_Leaves_A_Deleted_Seed_User_Deleted_And_Does_Not_Throw()
    {
        using var db = new SqliteTestDatabase();
        var seed = DevelopmentSeedData.Users.First(u => u.Email == DevelopmentSeedCredentials.OwnerAnahitEmail);
        var anonymizedEmail = $"deleted-{seed.Id}@deleted.invalid";
        var deletedAt = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

        await using (var context = db.CreateContext())
        {
            await CreateRunner(context).RunAsync(CancellationToken.None);
        }

        int usersBefore;
        await using (var context = db.CreateContext())
        {
            var user = await context.Users.SingleAsync(u => u.Id == seed.Id);
            user.Email = anonymizedEmail;
            user.DeletedAt = deletedAt;
            await context.SaveChangesAsync();
            usersBefore = await context.Users.CountAsync();
        }

        await using (var context = db.CreateContext())
        {
            await CreateRunner(context).RunAsync(CancellationToken.None);
        }

        await using var verify = db.CreateContext();
        var after = await verify.Users.SingleAsync(u => u.Id == seed.Id);
        Assert.Equal(anonymizedEmail, after.Email);
        Assert.Equal(deletedAt, after.DeletedAt);
        Assert.Equal(usersBefore, await verify.Users.CountAsync());
    }
}
