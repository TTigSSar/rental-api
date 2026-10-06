using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Application.Services;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.DependencyInjection.DemoContentBootstrap;
using RentalPlatform.Infrastructure.DependencyInjection.DevelopmentSeed;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// Exercises DemoContentBootstrapRunner — the production-only mechanism that seeds an initial
// public catalogue (a showcase owner + their Approved listings/images) on a fresh database, since
// (unlike Development) Production never runs the dev seed and would otherwise show an empty
// marketplace. Goes through the real IPasswordHasher (BCrypt) and a real AppDbContext backed by an
// in-memory SQLite database; the HttpClient is given a fake handler so image "downloads" never hit
// the real network and stay deterministic.
public sealed class DemoContentBootstrapTests
{
    // Every category DevelopmentSeedData.Listings can reference, present up front — mirrors what
    // the SeedReferenceCategories migration guarantees on a real database before this bootstrap runs.
    private static async Task SeedCategoriesAsync(SqliteTestDatabase db)
    {
        var categories = DevelopmentSeedData.Categories
            .Select(c => new Category
            {
                Id = c.Id,
                Name = c.Name,
                Slug = c.Slug,
                IconName = c.IconName,
                ImageUrl = c.ImageUrl,
                DisplayOrder = c.DisplayOrder
            })
            .ToArray();

        await db.SeedAsync(categories);
    }

    private static DemoContentBootstrapRunner BuildRunner(
        SqliteTestDatabase db,
        HttpStatusCode imageResponseStatus = HttpStatusCode.ServiceUnavailable)
    {
        // One context shared by the runner and the home-point service it calls — exactly how DI
        // wires them at startup (both resolved from the same scope). Two contexts would make the
        // runner's uncommitted work invisible to the service.
        var context = db.CreateContext();

        return new DemoContentBootstrapRunner(
            context,
            new BcryptPasswordHasher(),
            new FakeFileStorageService(),
            new HttpClient(new FakeHttpMessageHandler(imageResponseStatus))
            {
                Timeout = TimeSpan.FromSeconds(5)
            },
            NullLogger<DemoContentBootstrapRunner>.Instance,
            new DistrictBoundaryProvider(),
            new HomePointService(
                new HomePointStore(context),
                new GeohashSnapper(),
                new DistrictBoundaryProvider(),
                new FakeNotificationEmitter()));
    }

    [Fact]
    public async Task Is_NoOp_When_Not_Enabled()
    {
        using var db = new SqliteTestDatabase();
        await SeedCategoriesAsync(db);
        var runner = BuildRunner(db);

        await runner.RunAsync(enabled: false, ownerEmail: "owner@dorent.am", ownerPassword: "SuperSecret123", CancellationToken.None);

        await using var verify = db.CreateContext();
        Assert.Empty(await verify.Users.ToListAsync());
        Assert.Empty(await verify.Listings.ToListAsync());
    }

    [Theory]
    [InlineData(null, "SomePassword1")]
    [InlineData("owner@dorent.am", null)]
    [InlineData("", "SomePassword1")]
    [InlineData("owner@dorent.am", "")]
    [InlineData(null, null)]
    public async Task Is_NoOp_When_Enabled_But_Owner_Credentials_Incomplete(string? email, string? password)
    {
        using var db = new SqliteTestDatabase();
        await SeedCategoriesAsync(db);
        var runner = BuildRunner(db);

        await runner.RunAsync(enabled: true, ownerEmail: email, ownerPassword: password, CancellationToken.None);

        await using var verify = db.CreateContext();
        Assert.Empty(await verify.Users.ToListAsync());
        Assert.Empty(await verify.Listings.ToListAsync());
    }

    [Fact]
    public async Task Creates_Showcase_Owners_With_Hashed_Passwords_And_Only_Approved_Listings_With_Images()
    {
        using var db = new SqliteTestDatabase();
        await SeedCategoriesAsync(db);
        var runner = BuildRunner(db);

        await runner.RunAsync(enabled: true, ownerEmail: "Owner@DoRent.am", ownerPassword: "SuperSecret123", CancellationToken.None);

        await using var verify = db.CreateContext();

        // The base account from configuration, plus one per district the catalogue lands in — a
        // single owner would put the whole public catalogue on one pin (home-point model).
        var owners = await verify.Users.ToListAsync();
        var baseOwner = Assert.Single(owners, user => user.Email == "owner@dorent.am"); // normalized: trimmed + lowercased
        Assert.Contains(owners, user => user.Email == "owner.kentron@dorent.am");

        Assert.All(owners, owner =>
        {
            Assert.Equal(UserRole.User, owner.Role); // never Admin
            Assert.NotEqual("SuperSecret123", owner.PasswordHash); // never stored raw
            Assert.False(string.IsNullOrWhiteSpace(owner.PhoneNumber));
            Assert.False(string.IsNullOrWhiteSpace(owner.FirstName));
            Assert.False(string.IsNullOrWhiteSpace(owner.LastName));
        });

        // The configured password belongs to the BASE account and to nothing else. The per-district
        // accounts exist only to own listings so the catalogue map has more than one pin, and
        // handing them the operator's password would turn one knowingly-accepted live credential
        // (ADR-005) into thirteen — all with the same secret, all able to edit the storefront and
        // answer real renters. Each gets a random one instead, which is held by nobody.
        Assert.True(BCrypt.Net.BCrypt.Verify("SuperSecret123", baseOwner.PasswordHash));

        var districtOwners = owners.Where(user => user.Id != baseOwner.Id).ToList();
        Assert.NotEmpty(districtOwners);
        Assert.All(districtOwners, owner =>
        {
            Assert.False(
                BCrypt.Net.BCrypt.Verify("SuperSecret123", owner.PasswordHash),
                $"{owner.Email} accepts the configured showcase password — ADR-005's single-account trade-off just became a 13-account one.");

            // A real BCrypt hash of a real password, not an empty/placeholder credential that some
            // other code path might treat as "no password set".
            Assert.StartsWith("$2", owner.PasswordHash, StringComparison.Ordinal);
        });

        // Deliberately NOT asserted here: that the twelve random passwords differ from each other.
        // BCrypt salts every hash, so twelve identical passwords also produce twelve different
        // hashes — a distinctness assertion on PasswordHash would pass either way and would be
        // decoration. The per-account generation is visible in GenerateUnusableRandomPassword's
        // single call site instead; what a test CAN see from outside is the property above: the
        // published credential does not open these accounts.

        var expectedApprovedCount = DevelopmentSeedData.Listings.Count(l => l.Status == ListingStatus.Approved);
        var listings = await verify.Listings.ToListAsync();
        Assert.Equal(expectedApprovedCount, listings.Count);
        Assert.All(listings, l => Assert.Equal(ListingStatus.Approved, l.Status));

        // The point of the redistribution: the showcase sits in many districts, not one. Every
        // listing has a location (inherited from its owner's home point) and more than one district
        // is represented.
        Assert.All(listings, l => Assert.NotNull(l.Latitude));
        Assert.True(
            listings.Select(l => l.OwnerId).Distinct().Count() > 1,
            "The showcase catalogue collapsed onto a single owner — and therefore a single map pin.");
        Assert.True(
            listings.Where(l => l.DistrictId is not null).Select(l => l.DistrictId).Distinct().Count() >= 10,
            "The showcase catalogue does not span the districts its listings came from.");

        // The base owner exists and works as a login, but ends up holding only what could not be
        // placed in a district.
        Assert.NotEqual(Guid.Empty, baseOwner.Id);

        // No Draft/PendingApproval/Rejected seed listing ever made it in.
        var nonApprovedSeedIds = DevelopmentSeedData.Listings
            .Where(l => l.Status != ListingStatus.Approved)
            .Select(l => l.Id)
            .ToHashSet();
        Assert.DoesNotContain(listings, l => nonApprovedSeedIds.Contains(l.Id));

        // Every approved listing's images were created, with the download failure falling back
        // to the local SVG (the fake handler returns 503 for every request).
        var images = await verify.ListingImages.ToListAsync();
        var expectedImageCount = DevelopmentSeedData.ListingImages
            .Count(img => DevelopmentSeedData.Listings.Any(l => l.Id == img.ListingId && l.Status == ListingStatus.Approved));
        Assert.Equal(expectedImageCount, images.Count);
        Assert.All(images, img => Assert.StartsWith("/assets/categories/", img.Url));
    }

    [Fact]
    public async Task Second_Run_Is_A_Clean_NoOp()
    {
        using var db = new SqliteTestDatabase();
        await SeedCategoriesAsync(db);

        await BuildRunner(db).RunAsync(enabled: true, ownerEmail: "owner@dorent.am", ownerPassword: "SuperSecret123", CancellationToken.None);

        int usersAfterFirst, listingsAfterFirst, imagesAfterFirst;
        await using (var verify1 = db.CreateContext())
        {
            usersAfterFirst = await verify1.Users.CountAsync();
            listingsAfterFirst = await verify1.Listings.CountAsync();
            imagesAfterFirst = await verify1.ListingImages.CountAsync();
        }

        // Second run — a brand-new runner instance, exactly as would happen on a container restart.
        await BuildRunner(db).RunAsync(enabled: true, ownerEmail: "owner@dorent.am", ownerPassword: "SuperSecret123", CancellationToken.None);

        await using var verify2 = db.CreateContext();
        Assert.Equal(usersAfterFirst, await verify2.Users.CountAsync());
        Assert.Equal(listingsAfterFirst, await verify2.Listings.CountAsync());
        Assert.Equal(imagesAfterFirst, await verify2.ListingImages.CountAsync());
    }

    [Fact]
    public async Task Never_Modifies_A_Listing_A_Real_User_Has_Since_Edited()
    {
        using var db = new SqliteTestDatabase();
        await SeedCategoriesAsync(db);

        await BuildRunner(db).RunAsync(enabled: true, ownerEmail: "owner@dorent.am", ownerPassword: "SuperSecret123", CancellationToken.None);

        Guid editedListingId;
        await using (var mutate = db.CreateContext())
        {
            var listing = await mutate.Listings.FirstAsync();
            editedListingId = listing.Id;
            listing.Title = "Edited by the real showcase owner";
            listing.PricePerDay = 999m;
            await mutate.SaveChangesAsync();
        }

        // Re-run: must not touch the row a "real user" (here: our direct edit, standing in for the
        // owner using the normal owner UI) has since changed.
        await BuildRunner(db).RunAsync(enabled: true, ownerEmail: "owner@dorent.am", ownerPassword: "SuperSecret123", CancellationToken.None);

        await using var verify = db.CreateContext();
        var reloaded = await verify.Listings.SingleAsync(l => l.Id == editedListingId);
        Assert.Equal("Edited by the real showcase owner", reloaded.Title);
        Assert.Equal(999m, reloaded.PricePerDay);
    }

    [Fact]
    public async Task Skips_A_Listing_Whose_Category_Is_Missing_Without_Throwing()
    {
        using var db = new SqliteTestDatabase();
        // Deliberately do NOT seed categories — every listing should be skipped, not throw.
        var runner = BuildRunner(db);

        await runner.RunAsync(enabled: true, ownerEmail: "owner@dorent.am", ownerPassword: "SuperSecret123", CancellationToken.None);

        await using var verify = db.CreateContext();
        // The owner accounts are still created (they are derived from the seed data, not from what
        // could actually be inserted) — but no listing has a valid category, so none is created.
        Assert.NotEmpty(await verify.Users.ToListAsync());
        Assert.Empty(await verify.Listings.ToListAsync());
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public FakeHttpMessageHandler(HttpStatusCode status) => _status = status;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status));
    }
}
