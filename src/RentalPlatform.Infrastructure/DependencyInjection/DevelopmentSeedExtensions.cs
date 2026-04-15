using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;

namespace RentalPlatform.Infrastructure.DependencyInjection;

public static class DevelopmentSeedExtensions
{
    private sealed record SeedCategory(Guid Id, string Name, string Slug);
    private sealed record SeedUser(Guid Id, string Email, string FirstName, string LastName, UserRole Role);
    private sealed record SeedListing(
        Guid Id,
        string Title,
        string Description,
        string CategorySlug,
        string OwnerEmail,
        decimal PricePerDay,
        string Currency,
        string Country,
        string City,
        string AddressLine,
        decimal Latitude,
        decimal Longitude,
        ListingStatus Status);

    private static readonly SeedCategory[] DefaultCategories =
    [
        new(new Guid("f450befb-b2af-4f1e-8f34-0f9fd70d9c96"), "Apartments", "apartments"),
        new(new Guid("d1f1e1d9-3a9d-4cde-9a7a-213844e0f4d8"), "Houses", "houses"),
        new(new Guid("cb2f147c-95f2-4f69-b065-c01ee3464f3a"), "Cars", "cars"),
        new(new Guid("8f4b38ff-2e27-4f23-957c-2ea1a2f3554a"), "Electronics", "electronics"),
        new(new Guid("d41f8b63-e019-4f8e-8bc6-0ab90d6f8d0f"), "Toys", "toys"),
        new(new Guid("f91a1f36-2063-4a7f-b4b1-65f30c6f6ef5"), "Tools", "tools")
    ];

    private static readonly SeedUser[] DefaultUsers =
    [
        new(
            new Guid("ddae2f7f-3dda-488c-9b45-f8cc2052d0cf"),
            "demo.user@local.test",
            "Demo",
            "User",
            UserRole.User),
        new(
            new Guid("5f04518c-030b-4a6a-a0ca-3e2e4edca98c"),
            "demo.admin@local.test",
            "Demo",
            "Admin",
            UserRole.Admin)
    ];

    private static readonly SeedListing[] DefaultListings =
    [
        new(
            new Guid("a8b2bda2-5f5d-4f6a-a500-4e7ee224f9f9"),
            "City Center Apartment",
            "Modern one-bedroom apartment with fast Wi-Fi and self check-in near downtown attractions.",
            "apartments",
            "demo.admin@local.test",
            65m,
            "USD",
            "Armenia",
            "Yerevan",
            "15 Abovyan St",
            40.1792m,
            44.4991m,
            ListingStatus.Approved),
        new(
            new Guid("03d43cf7-71aa-4994-98ac-f42a7c6e1d9f"),
            "Family SUV Rental",
            "Comfortable 7-seat SUV, ideal for weekend trips and airport transfers.",
            "cars",
            "demo.user@local.test",
            95m,
            "USD",
            "Armenia",
            "Yerevan",
            "21 Tigran Mets Ave",
            40.1771m,
            44.5126m,
            ListingStatus.PendingApproval),
        new(
            new Guid("cb6a43f9-c7e7-4f76-9a9e-65f4ebbff3f0"),
            "Vintage Tool Set Collection",
            "Large collection of vintage tools in used condition. Listing prepared for review testing.",
            "tools",
            "demo.user@local.test",
            30m,
            "USD",
            "Armenia",
            "Gyumri",
            "6 Rustaveli St",
            40.7896m,
            43.8475m,
            ListingStatus.Rejected)
    ];

    private const string DemoPassword = "LocalDemo123!";

    public static async Task SeedDevelopmentDataAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DevelopmentSeed");
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var existingCategories = await dbContext.Categories
            .Where(category => DefaultCategories.Select(seed => seed.Slug).Contains(category.Slug))
            .ToListAsync(cancellationToken);
        var categoriesBySlug = existingCategories.ToDictionary(category => category.Slug, StringComparer.OrdinalIgnoreCase);

        var missingCategories = DefaultCategories
            .Where(seedCategory => !categoriesBySlug.ContainsKey(seedCategory.Slug))
            .Select(seedCategory => new Category
            {
                Id = seedCategory.Id,
                Name = seedCategory.Name,
                Slug = seedCategory.Slug
            })
            .ToArray();

        foreach (var category in missingCategories)
        {
            categoriesBySlug[category.Slug] = category;
        }

        if (missingCategories.Length > 0)
        {
            await dbContext.Categories.AddRangeAsync(missingCategories, cancellationToken);
        }

        var seedEmails = DefaultUsers.Select(seedUser => seedUser.Email).Select(NormalizeEmail).ToArray();
        var existingUsers = await dbContext.Users
            .Where(user => seedEmails.Contains(user.Email))
            .ToListAsync(cancellationToken);
        var usersByEmail = existingUsers.ToDictionary(user => NormalizeEmail(user.Email), StringComparer.OrdinalIgnoreCase);

        var missingUsers = DefaultUsers
            .Where(seedUser => !usersByEmail.ContainsKey(NormalizeEmail(seedUser.Email)))
            .Select(seedUser =>
            {
                var user = new User
                {
                    Id = seedUser.Id,
                    Email = NormalizeEmail(seedUser.Email),
                    PasswordHash = passwordHasher.HashPassword(DemoPassword),
                    FirstName = seedUser.FirstName,
                    LastName = seedUser.LastName,
                    PhoneNumber = null,
                    PreferredLanguage = "en",
                    ExternalAuthProvider = null,
                    ExternalProviderId = null,
                    AvatarUrl = null,
                    CreatedAt = DateTime.UtcNow,
                    IsBlocked = false,
                    Role = seedUser.Role
                };

                usersByEmail[user.Email] = user;
                return user;
            })
            .ToArray();

        if (missingUsers.Length > 0)
        {
            await dbContext.Users.AddRangeAsync(missingUsers, cancellationToken);
        }

        var listingIds = DefaultListings.Select(listing => listing.Id).ToArray();
        var existingListings = await dbContext.Listings
            .Where(listing => listingIds.Contains(listing.Id))
            .Select(listing => listing.Id)
            .ToListAsync(cancellationToken);
        var existingListingIdSet = existingListings.ToHashSet();

        var now = DateTime.UtcNow;
        var missingListings = DefaultListings
            .Where(seedListing => !existingListingIdSet.Contains(seedListing.Id))
            .Select(seedListing =>
            {
                var owner = usersByEmail[NormalizeEmail(seedListing.OwnerEmail)];
                var category = categoriesBySlug[seedListing.CategorySlug];
                return new Listing
                {
                    Id = seedListing.Id,
                    OwnerId = owner.Id,
                    CategoryId = category.Id,
                    Title = seedListing.Title,
                    Description = seedListing.Description,
                    PricePerDay = seedListing.PricePerDay,
                    Currency = seedListing.Currency,
                    Country = seedListing.Country,
                    City = seedListing.City,
                    AddressLine = seedListing.AddressLine,
                    Latitude = seedListing.Latitude,
                    Longitude = seedListing.Longitude,
                    Status = seedListing.Status,
                    CreatedAt = now.AddDays(-10),
                    UpdatedAt = now.AddDays(-1)
                };
            })
            .ToArray();

        if (missingListings.Length > 0)
        {
            await dbContext.Listings.AddRangeAsync(missingListings, cancellationToken);
        }

        var existingImageListingIds = await dbContext.ListingImages
            .Where(image => listingIds.Contains(image.ListingId))
            .Select(image => image.ListingId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var existingImageListingSet = existingImageListingIds.ToHashSet();

        var missingImages = DefaultListings
            .Where(seedListing => !existingImageListingSet.Contains(seedListing.Id))
            .Select(seedListing => new ListingImage
            {
                Id = Guid.NewGuid(),
                ListingId = seedListing.Id,
                Url = $"/uploads/listings/dev-{seedListing.Id:N}.jpg",
                IsPrimary = true,
                SortOrder = 0
            })
            .ToArray();

        if (missingImages.Length > 0)
        {
            await dbContext.ListingImages.AddRangeAsync(missingImages, cancellationToken);
        }

        var demoUser = usersByEmail[NormalizeEmail("demo.user@local.test")];
        var approvedListingId = DefaultListings.Single(listing => listing.Status == ListingStatus.Approved).Id;

        var hasFavorite = await dbContext.Favorites
            .AnyAsync(favorite => favorite.UserId == demoUser.Id && favorite.ListingId == approvedListingId, cancellationToken);
        var insertedFavorites = 0;
        if (!hasFavorite)
        {
            await dbContext.Favorites.AddAsync(new Favorite
            {
                Id = Guid.NewGuid(),
                UserId = demoUser.Id,
                ListingId = approvedListingId,
                CreatedAt = now
            }, cancellationToken);
            insertedFavorites = 1;
        }

        var hasBooking = await dbContext.Bookings
            .AnyAsync(booking =>
                booking.RenterId == demoUser.Id &&
                booking.ListingId == approvedListingId &&
                booking.Status == BookingStatus.Pending, cancellationToken);
        var insertedBookings = 0;
        if (!hasBooking)
        {
            await dbContext.Bookings.AddAsync(new Booking
            {
                Id = Guid.NewGuid(),
                ListingId = approvedListingId,
                RenterId = demoUser.Id,
                StartDate = DateOnly.FromDateTime(now.Date.AddDays(7)),
                EndDate = DateOnly.FromDateTime(now.Date.AddDays(10)),
                TotalPrice = 195m,
                Status = BookingStatus.Pending,
                ExpiresAt = now.AddDays(2),
                CreatedAt = now,
                UpdatedAt = now
            }, cancellationToken);
            insertedBookings = 1;
        }

        if (missingCategories.Length == 0 &&
            missingUsers.Length == 0 &&
            missingListings.Length == 0 &&
            missingImages.Length == 0 &&
            insertedFavorites == 0 &&
            insertedBookings == 0)
        {
            logger.LogInformation("Development seed skipped. Demo data already present.");
            return;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Development seed completed. Categories: {Categories}, Users: {Users}, Listings: {Listings}, Images: {Images}, Favorites: {Favorites}, Bookings: {Bookings}. Demo password: {DemoPassword}",
            missingCategories.Length,
            missingUsers.Length,
            missingListings.Length,
            missingImages.Length,
            insertedFavorites,
            insertedBookings,
            DemoPassword);
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
