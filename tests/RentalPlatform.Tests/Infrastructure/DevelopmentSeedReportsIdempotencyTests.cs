using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Infrastructure.DependencyInjection.DevelopmentSeed;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// Admin console Phase 4: the dev seed's report rows (DevelopmentSeedRunner.SeedReportsAsync)
// must be idempotent — re-running the seed against a database that already has them must not
// duplicate any row. This exercises the REAL DevelopmentSeedRunner.RunAsync end to end (not just
// the reports step in isolation) so the assertion reflects what actually happens at app startup.
// The dev seed's outbound image downloads are intercepted by a fake handler that always returns
// 404 — SeedImageResolver falls back to the local asset path on any non-success response, so this
// stays fast and network-free while still exercising the exact same code path production takes.
public sealed class DevelopmentSeedReportsIdempotencyTests
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
        new HttpClient(new NeverSucceedsHandler()));

    [Fact]
    public async Task RunAsync_Seeds_Reports_Spanning_Every_Severity_And_Status_Exactly_Once()
    {
        using var db = new SqliteTestDatabase();

        await using (var context = db.CreateContext())
        {
            await CreateRunner(context).RunAsync(CancellationToken.None);
        }

        await using var verify = db.CreateContext();
        var reports = await verify.Reports.ToListAsync();

        Assert.Equal(7, reports.Count);
        Assert.Equal(7, reports.Select(r => r.Id).Distinct().Count()); // no duplicate ids
        Assert.Contains(reports, r => r.Severity == RentalPlatform.Domain.Enums.ReportSeverity.Low);
        Assert.Contains(reports, r => r.Severity == RentalPlatform.Domain.Enums.ReportSeverity.Medium);
        Assert.Contains(reports, r => r.Severity == RentalPlatform.Domain.Enums.ReportSeverity.High);
        Assert.Contains(reports, r => r.Status == RentalPlatform.Domain.Enums.ReportStatus.Open);
        Assert.Contains(reports, r => r.Status == RentalPlatform.Domain.Enums.ReportStatus.Resolved);
        Assert.Contains(reports, r => r.Status == RentalPlatform.Domain.Enums.ReportStatus.Dismissed);
    }

    [Fact]
    public async Task RunAsync_Run_Twice_Does_Not_Duplicate_Reports()
    {
        using var db = new SqliteTestDatabase();

        await using (var context = db.CreateContext())
        {
            await CreateRunner(context).RunAsync(CancellationToken.None);
        }

        int firstRunCount;
        await using (var check = db.CreateContext())
        {
            firstRunCount = await check.Reports.CountAsync();
        }

        // Re-run the ENTIRE seed pipeline against the same database — the real startup scenario
        // (app restarts against a DB that already has demo data).
        await using (var context = db.CreateContext())
        {
            await CreateRunner(context).RunAsync(CancellationToken.None);
        }

        await using var verify = db.CreateContext();
        var secondRunCount = await verify.Reports.CountAsync();

        Assert.Equal(7, firstRunCount);
        Assert.Equal(firstRunCount, secondRunCount);
        Assert.Equal(7, await verify.Reports.Select(r => r.Id).Distinct().CountAsync());
    }
}
