using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.DependencyInjection.DemoContentBootstrap;
using RentalPlatform.Infrastructure.DependencyInjection.DevelopmentSeed;
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
        HttpStatusCode imageResponseStatus = HttpStatusCode.ServiceUnavailable) =>
        new(
            db.CreateContext(),
            new BcryptPasswordHasher(),
            new FakeFileStorageService(),
            new HttpClient(new FakeHttpMessageHandler(imageResponseStatus))
            {
                Timeout = TimeSpan.FromSeconds(5)
            },
            NullLogger<DemoContentBootstrapRunner>.Instance);

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
    public async Task Creates_Showcase_Owner_With_Hashed_Password_And_Only_Approved_Listings_With_Images()
    {
        using var db = new SqliteTestDatabase();
        await SeedCategoriesAsync(db);
        var runner = BuildRunner(db);

        await runner.RunAsync(enabled: true, ownerEmail: "Owner@DoRent.am", ownerPassword: "SuperSecret123", CancellationToken.None);

        await using var verify = db.CreateContext();

        var owner = Assert.Single(await verify.Users.ToListAsync());
        Assert.Equal("owner@dorent.am", owner.Email); // normalized: trimmed + lowercased
        Assert.Equal(UserRole.User, owner.Role); // never Admin
        Assert.NotEqual("SuperSecret123", owner.PasswordHash); // never stored raw
        Assert.True(BCrypt.Net.BCrypt.Verify("SuperSecret123", owner.PasswordHash));
        Assert.False(string.IsNullOrWhiteSpace(owner.PhoneNumber));
        Assert.False(string.IsNullOrWhiteSpace(owner.FirstName));
        Assert.False(string.IsNullOrWhiteSpace(owner.LastName));

        var expectedApprovedCount = DevelopmentSeedData.Listings.Count(l => l.Status == ListingStatus.Approved);
        var listings = await verify.Listings.ToListAsync();
        Assert.Equal(expectedApprovedCount, listings.Count);
        Assert.All(listings, l => Assert.Equal(ListingStatus.Approved, l.Status));
        Assert.All(listings, l => Assert.Equal(owner.Id, l.OwnerId));

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
        Assert.Single(await verify.Users.ToListAsync()); // owner is still created
        Assert.Empty(await verify.Listings.ToListAsync()); // but no listing has a valid category
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
