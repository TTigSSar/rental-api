using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Infrastructure.Persistence;

namespace RentalPlatform.Infrastructure.DependencyInjection.DevelopmentSeed;

/// <summary>
/// Orchestrates development-only database seeding. Every step is idempotent:
/// existing rows (by fixed seed GUIDs or slug/email) are detected and skipped,
/// so repeated startups never duplicate data.
/// </summary>
internal sealed class DevelopmentSeedRunner
{
    private readonly AppDbContext _dbContext;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ILogger<DevelopmentSeedRunner> _logger;

    public DevelopmentSeedRunner(
        AppDbContext dbContext,
        IPasswordHasher passwordHasher,
        ILogger<DevelopmentSeedRunner> logger)
    {
        _dbContext = dbContext;
        _passwordHasher = passwordHasher;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);

        var insertedCategories = await SeedCategoriesAsync(cancellationToken);
        var (userByEmail, insertedUsers) = await SeedUsersAsync(now, cancellationToken);
        var insertedListings = await SeedListingsAsync(userByEmail, now, cancellationToken);
        var insertedImages = await SeedListingImagesAsync(cancellationToken);
        var insertedFavorites = await SeedFavoritesAsync(userByEmail, now, cancellationToken);
        var insertedBookings = await SeedBookingsAsync(userByEmail, today, now, cancellationToken);
        var insertedReviews = await SeedReviewsAsync(userByEmail, now, cancellationToken);

        var totalInserted =
            insertedCategories + insertedUsers + insertedListings +
            insertedImages + insertedFavorites + insertedBookings + insertedReviews;

        if (totalInserted == 0)
        {
            _logger.LogInformation("Development seed skipped: demo data already present.");
            return;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Development seed completed. Categories: {Categories}, Users: {Users}, Listings: {Listings}, Images: {Images}, Favorites: {Favorites}, Bookings: {Bookings}. Demo password for all demo accounts: {DemoPassword}",
            insertedCategories, insertedUsers, insertedListings,
            insertedImages, insertedFavorites, insertedBookings,
            DevelopmentSeedCredentials.Password);
    }

    private async Task<int> SeedCategoriesAsync(CancellationToken cancellationToken)
    {
        var seedSlugs = DevelopmentSeedData.Categories
            .Select(category => category.Slug)
            .ToArray();

        var existingBySlug = await _dbContext.Categories
            .Where(category => seedSlugs.Contains(category.Slug))
            .ToListAsync(cancellationToken);

        var existingSlugSet = existingBySlug
            .Select(category => category.Slug)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Insert categories that are missing entirely.
        var newRows = DevelopmentSeedData.Categories
            .Where(category => !existingSlugSet.Contains(category.Slug))
            .Select(category => new Category
            {
                Id = category.Id,
                Name = category.Name,
                Slug = category.Slug,
                IconName = category.IconName,
                ImageUrl = category.ImageUrl,
                DisplayOrder = category.DisplayOrder
            })
            .ToArray();

        if (newRows.Length > 0)
        {
            await _dbContext.Categories.AddRangeAsync(newRows, cancellationToken);
        }

        // Backfill visual metadata on existing rows that predate this field.
        var seedBySlug = DevelopmentSeedData.Categories
            .ToDictionary(category => category.Slug, StringComparer.OrdinalIgnoreCase);

        var updated = 0;
        foreach (var existing in existingBySlug)
        {
            if (!seedBySlug.TryGetValue(existing.Slug, out var seed))
            {
                continue;
            }

            var dirty = false;
            if (existing.IconName != seed.IconName)  { existing.IconName     = seed.IconName;     dirty = true; }
            if (existing.ImageUrl != seed.ImageUrl)  { existing.ImageUrl     = seed.ImageUrl;     dirty = true; }
            if (existing.DisplayOrder != seed.DisplayOrder) { existing.DisplayOrder = seed.DisplayOrder; dirty = true; }
            if (dirty) updated++;
        }

        return newRows.Length + updated;
    }

    private async Task<(IDictionary<string, User> ByEmail, int Changes)> SeedUsersAsync(
        DateTime now,
        CancellationToken cancellationToken)
    {
        var targetPassword = DevelopmentSeedCredentials.Password;

        var seedEmails = DevelopmentSeedData.Users
            .Select(user => NormalizeEmail(user.Email))
            .ToArray();

        var existingUsers = await _dbContext.Users
            .Where(user => seedEmails.Contains(user.Email))
            .ToListAsync(cancellationToken);

        var byEmail = existingUsers.ToDictionary(
            user => NormalizeEmail(user.Email),
            StringComparer.OrdinalIgnoreCase);

        var inserted = 0;
        var updated = 0;

        foreach (var seed in DevelopmentSeedData.Users)
        {
            var email = NormalizeEmail(seed.Email);

            if (byEmail.TryGetValue(email, out var existing))
            {
                // Reset password hash if it no longer matches the target demo password.
                // This ensures that changing DevelopmentSeedCredentials.Password takes
                // effect on existing rows without requiring a full database wipe.
                if (!_passwordHasher.VerifyPassword(targetPassword, existing.PasswordHash))
                {
                    existing.PasswordHash = _passwordHasher.HashPassword(targetPassword);
                    _logger.LogInformation("Demo seed: reset password hash for {Email}.", email);
                    updated++;
                }

                // Keep the role in sync with the seed definition.
                if (existing.Role != seed.Role)
                {
                    existing.Role = seed.Role;
                    updated++;
                }

                // Backfill phone number if the seed has one and the row is missing it.
                if (seed.PhoneNumber is not null && existing.PhoneNumber is null)
                {
                    existing.PhoneNumber = seed.PhoneNumber;
                    updated++;
                }

                continue;
            }

            var user = new User
            {
                Id = seed.Id,
                Email = email,
                PasswordHash = _passwordHasher.HashPassword(targetPassword),
                FirstName = seed.FirstName,
                LastName = seed.LastName,
                PhoneNumber = seed.PhoneNumber,
                PreferredLanguage = "en",
                ExternalAuthProvider = null,
                ExternalProviderId = null,
                AvatarUrl = null,
                CreatedAt = now,
                IsBlocked = seed.IsBlocked,
                Role = seed.Role
            };

            await _dbContext.Users.AddAsync(user, cancellationToken);
            byEmail[email] = user;
            inserted++;
        }

        if (inserted > 0)
            _logger.LogInformation("Demo seed: created {Count} user(s).", inserted);
        if (updated > 0)
            _logger.LogInformation("Demo seed: updated {Count} user(s) (password reset or role change).", updated);

        return (byEmail, inserted + updated);
    }

    private async Task<int> SeedListingsAsync(
        IDictionary<string, User> userByEmail,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var seedIds = DevelopmentSeedData.Listings.Select(listing => listing.Id).ToArray();
        var existingIds = await _dbContext.Listings
            .Where(listing => seedIds.Contains(listing.Id))
            .Select(listing => listing.Id)
            .ToListAsync(cancellationToken);
        var existingIdSet = existingIds.ToHashSet();

        var categoriesBySlug = DevelopmentSeedData.Categories
            .ToDictionary(category => category.Slug, StringComparer.OrdinalIgnoreCase);

        var newRows = new List<Listing>();
        foreach (var seed in DevelopmentSeedData.Listings)
        {
            if (existingIdSet.Contains(seed.Id))
            {
                continue;
            }

            if (!userByEmail.TryGetValue(NormalizeEmail(seed.OwnerEmail), out var owner))
            {
                _logger.LogWarning(
                    "Skipping seed listing '{Title}': owner {OwnerEmail} is missing.",
                    seed.Title, seed.OwnerEmail);
                continue;
            }

            if (!categoriesBySlug.TryGetValue(seed.CategorySlug, out var category))
            {
                _logger.LogWarning(
                    "Skipping seed listing '{Title}': category '{Slug}' is missing.",
                    seed.Title, seed.CategorySlug);
                continue;
            }

            newRows.Add(new Listing
            {
                Id = seed.Id,
                OwnerId = owner.Id,
                CategoryId = category.Id,
                Title = seed.Title,
                Description = seed.Description,
                PricePerDay = seed.PricePerDay,
                Currency = seed.Currency,
                Country = seed.Country,
                City = seed.City,
                AddressLine = seed.AddressLine,
                Latitude = seed.Latitude,
                Longitude = seed.Longitude,
                AgeFromMonths = seed.AgeFromMonths,
                AgeToMonths = seed.AgeToMonths,
                Condition = seed.Condition,
                HygieneNotes = seed.HygieneNotes,
                SafetyNotes = seed.SafetyNotes,
                DepositAmount = seed.DepositAmount,
                RejectionReason = seed.RejectionReason,
                Status = seed.Status,
                CreatedAt = now.AddDays(-seed.CreatedDaysAgo),
                UpdatedAt = now.AddDays(-seed.UpdatedDaysAgo)
            });
        }

        if (newRows.Count > 0)
        {
            await _dbContext.Listings.AddRangeAsync(newRows, cancellationToken);
        }

        return newRows.Count;
    }

    private async Task<int> SeedListingImagesAsync(CancellationToken cancellationToken)
    {
        var seedIds = DevelopmentSeedData.ListingImages.Select(image => image.Id).ToArray();
        var existingIds = await _dbContext.ListingImages
            .Where(image => seedIds.Contains(image.Id))
            .Select(image => image.Id)
            .ToListAsync(cancellationToken);
        var existingIdSet = existingIds.ToHashSet();

        // Fetch the set of listing ids actually present in the DB so we never insert an orphan image row.
        var referencedListingIds = DevelopmentSeedData.ListingImages
            .Select(image => image.ListingId)
            .Distinct()
            .ToArray();
        var presentListingIds = (await _dbContext.Listings
            .Where(listing => referencedListingIds.Contains(listing.Id))
            .Select(listing => listing.Id)
            .ToListAsync(cancellationToken))
            .ToHashSet();

        // Include listings queued for insert in this same run (not yet persisted).
        foreach (var entry in _dbContext.ChangeTracker.Entries<Listing>())
        {
            if (entry.State == EntityState.Added)
            {
                presentListingIds.Add(entry.Entity.Id);
            }
        }

        var newRows = DevelopmentSeedData.ListingImages
            .Where(image => !existingIdSet.Contains(image.Id) && presentListingIds.Contains(image.ListingId))
            .Select(image => new ListingImage
            {
                Id = image.Id,
                ListingId = image.ListingId,
                Url = image.Url,
                IsPrimary = image.IsPrimary,
                SortOrder = image.SortOrder
            })
            .ToArray();

        if (newRows.Length > 0)
        {
            await _dbContext.ListingImages.AddRangeAsync(newRows, cancellationToken);
        }

        return newRows.Length;
    }

    private async Task<int> SeedFavoritesAsync(
        IDictionary<string, User> userByEmail,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var seedIds = DevelopmentSeedData.Favorites.Select(favorite => favorite.Id).ToArray();
        var existingIds = await _dbContext.Favorites
            .Where(favorite => seedIds.Contains(favorite.Id))
            .Select(favorite => favorite.Id)
            .ToListAsync(cancellationToken);
        var existingIdSet = existingIds.ToHashSet();

        // Guard against (UserId, ListingId) uniqueness: if a row with the same pair exists under a different id,
        // do not insert a duplicate.
        var existingPairs = (await _dbContext.Favorites
            .Where(favorite => DevelopmentSeedData.Favorites
                .Select(seed => seed.ListingId)
                .Contains(favorite.ListingId))
            .Select(favorite => new { favorite.UserId, favorite.ListingId })
            .ToListAsync(cancellationToken))
            .Select(pair => (pair.UserId, pair.ListingId))
            .ToHashSet();

        var presentListingIds = await ResolvePresentListingIdsAsync(
            DevelopmentSeedData.Favorites.Select(favorite => favorite.ListingId).Distinct().ToArray(),
            cancellationToken);

        var newRows = new List<Favorite>();
        foreach (var seed in DevelopmentSeedData.Favorites)
        {
            if (existingIdSet.Contains(seed.Id))
            {
                continue;
            }

            if (!userByEmail.TryGetValue(NormalizeEmail(seed.UserEmail), out var user))
            {
                _logger.LogWarning(
                    "Skipping seed favorite {Id}: user {Email} is missing.",
                    seed.Id, seed.UserEmail);
                continue;
            }

            if (!presentListingIds.Contains(seed.ListingId))
            {
                _logger.LogWarning(
                    "Skipping seed favorite {Id}: listing {ListingId} is missing.",
                    seed.Id, seed.ListingId);
                continue;
            }

            if (existingPairs.Contains((user.Id, seed.ListingId)))
            {
                continue;
            }

            newRows.Add(new Favorite
            {
                Id = seed.Id,
                UserId = user.Id,
                ListingId = seed.ListingId,
                CreatedAt = now.AddDays(-seed.CreatedDaysAgo)
            });
            existingPairs.Add((user.Id, seed.ListingId));
        }

        if (newRows.Count > 0)
        {
            await _dbContext.Favorites.AddRangeAsync(newRows, cancellationToken);
        }

        return newRows.Count;
    }

    private async Task<int> SeedBookingsAsync(
        IDictionary<string, User> userByEmail,
        DateOnly today,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var seedIds = DevelopmentSeedData.Bookings.Select(booking => booking.Id).ToArray();
        var existingIds = await _dbContext.Bookings
            .Where(booking => seedIds.Contains(booking.Id))
            .Select(booking => booking.Id)
            .ToListAsync(cancellationToken);
        var existingIdSet = existingIds.ToHashSet();

        var listingPrices = DevelopmentSeedData.Listings
            .ToDictionary(listing => listing.Id, listing => listing.PricePerDay);

        // Fill in prices for any listing ids referenced by bookings but not defined in SeedData.Listings
        // (e.g. pre-existing CityCenterApartment kept from earlier seed runs).
        var externalListingIds = DevelopmentSeedData.Bookings
            .Select(booking => booking.ListingId)
            .Where(id => !listingPrices.ContainsKey(id))
            .Distinct()
            .ToArray();

        if (externalListingIds.Length > 0)
        {
            var externalListings = await _dbContext.Listings
                .Where(listing => externalListingIds.Contains(listing.Id))
                .Select(listing => new { listing.Id, listing.PricePerDay })
                .ToListAsync(cancellationToken);

            foreach (var row in externalListings)
            {
                listingPrices[row.Id] = row.PricePerDay;
            }
        }

        var newRows = new List<Booking>();
        foreach (var seed in DevelopmentSeedData.Bookings)
        {
            if (existingIdSet.Contains(seed.Id))
            {
                continue;
            }

            if (!userByEmail.TryGetValue(NormalizeEmail(seed.RenterEmail), out var renter))
            {
                _logger.LogWarning(
                    "Skipping seed booking {Id}: renter {Email} is missing.",
                    seed.Id, seed.RenterEmail);
                continue;
            }

            if (!listingPrices.TryGetValue(seed.ListingId, out var pricePerDay))
            {
                _logger.LogWarning(
                    "Skipping seed booking {Id}: listing {ListingId} is missing.",
                    seed.Id, seed.ListingId);
                continue;
            }

            var startDate = today.AddDays(seed.StartDaysFromToday);
            var endDate = startDate.AddDays(seed.DurationDays - 1);
            var inclusiveDays = endDate.DayNumber - startDate.DayNumber + 1;
            var totalPrice = inclusiveDays * pricePerDay;
            var createdAt = now.AddDays(-seed.CreatedDaysAgo);

            newRows.Add(new Booking
            {
                Id = seed.Id,
                ListingId = seed.ListingId,
                RenterId = renter.Id,
                StartDate = startDate,
                EndDate = endDate,
                TotalPrice = totalPrice,
                Status = seed.Status,
                ExpiresAt = now.AddHours(seed.ExpiresAtHoursFromNow),
                CreatedAt = createdAt,
                UpdatedAt = now
            });
        }

        if (newRows.Count > 0)
        {
            await _dbContext.Bookings.AddRangeAsync(newRows, cancellationToken);
        }

        return newRows.Count;
    }

    private async Task<int> SeedReviewsAsync(
        IDictionary<string, User> userByEmail,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var bookingsById = DevelopmentSeedData.Bookings.ToDictionary(b => b.Id);
        var listingsById = DevelopmentSeedData.Listings.ToDictionary(l => l.Id);

        // Booking ids present in the DB plus those queued for insert in this same run.
        var presentBookingIds = (await _dbContext.Bookings
            .Select(b => b.Id)
            .ToListAsync(cancellationToken)).ToHashSet();
        foreach (var entry in _dbContext.ChangeTracker.Entries<Booking>())
        {
            if (entry.State == EntityState.Added)
            {
                presentBookingIds.Add(entry.Entity.Id);
            }
        }

        User? Renter(SeedBookingRef booking) =>
            userByEmail.TryGetValue(NormalizeEmail(booking.RenterEmail), out var u) ? u : null;

        User? Owner(SeedBookingRef booking) =>
            listingsById.TryGetValue(booking.ListingId, out var listing)
            && userByEmail.TryGetValue(NormalizeEmail(listing.OwnerEmail), out var u) ? u : null;

        var added = 0;

        // --- Toy reviews ---
        var toyIds = DevelopmentSeedData.ToyReviews.Select(r => r.Id).ToArray();
        var existingToy = (await _dbContext.ToyReviews
            .Where(r => toyIds.Contains(r.Id)).Select(r => r.Id).ToListAsync(cancellationToken)).ToHashSet();
        foreach (var seed in DevelopmentSeedData.ToyReviews)
        {
            if (existingToy.Contains(seed.Id) || !presentBookingIds.Contains(seed.BookingId)) continue;
            if (!bookingsById.TryGetValue(seed.BookingId, out var b)) continue;
            var renter = Renter(new SeedBookingRef(b.ListingId, b.RenterEmail));
            if (renter is null) continue;

            await _dbContext.ToyReviews.AddAsync(new ToyReview
            {
                Id = seed.Id,
                BookingId = seed.BookingId,
                ListingId = b.ListingId,
                ReviewerId = renter.Id,
                OverallRating = seed.Overall,
                ConditionRating = seed.Condition,
                CleanlinessRating = seed.Cleanliness,
                ValueForMoneyRating = seed.Value,
                FunPlayValueRating = seed.Fun,
                DescriptionAccuracyRating = seed.Description,
                Comment = seed.Comment,
                CreatedAt = now.AddDays(-seed.CreatedDaysAgo)
            }, cancellationToken);
            added++;
        }

        // --- Owner reviews (renter → owner) ---
        var ownerIds = DevelopmentSeedData.OwnerReviews.Select(r => r.Id).ToArray();
        var existingOwner = (await _dbContext.OwnerReviews
            .Where(r => ownerIds.Contains(r.Id)).Select(r => r.Id).ToListAsync(cancellationToken)).ToHashSet();
        foreach (var seed in DevelopmentSeedData.OwnerReviews)
        {
            if (existingOwner.Contains(seed.Id) || !presentBookingIds.Contains(seed.BookingId)) continue;
            if (!bookingsById.TryGetValue(seed.BookingId, out var b)) continue;
            var bookingRef = new SeedBookingRef(b.ListingId, b.RenterEmail);
            var renter = Renter(bookingRef);
            var owner = Owner(bookingRef);
            if (renter is null || owner is null) continue;

            await _dbContext.OwnerReviews.AddAsync(new OwnerReview
            {
                Id = seed.Id,
                BookingId = seed.BookingId,
                OwnerId = owner.Id,
                ReviewerId = renter.Id,
                CommunicationRating = seed.Communication,
                PickupHandoverRating = seed.Pickup,
                FriendlinessRating = seed.Friendliness,
                Comment = seed.Comment,
                CreatedAt = now.AddDays(-seed.CreatedDaysAgo)
            }, cancellationToken);
            added++;
        }

        // --- Renter reviews (owner → renter) ---
        var renterIds = DevelopmentSeedData.RenterReviews.Select(r => r.Id).ToArray();
        var existingRenter = (await _dbContext.RenterReviews
            .Where(r => renterIds.Contains(r.Id)).Select(r => r.Id).ToListAsync(cancellationToken)).ToHashSet();
        foreach (var seed in DevelopmentSeedData.RenterReviews)
        {
            if (existingRenter.Contains(seed.Id) || !presentBookingIds.Contains(seed.BookingId)) continue;
            if (!bookingsById.TryGetValue(seed.BookingId, out var b)) continue;
            var bookingRef = new SeedBookingRef(b.ListingId, b.RenterEmail);
            var renter = Renter(bookingRef);
            var owner = Owner(bookingRef);
            if (renter is null || owner is null) continue;

            await _dbContext.RenterReviews.AddAsync(new RenterReview
            {
                Id = seed.Id,
                BookingId = seed.BookingId,
                RenterId = renter.Id,
                ReviewerId = owner.Id,
                CommunicationRating = seed.Communication,
                ReturnedOnTimeRating = seed.Returned,
                CareOfToyRating = seed.Care,
                WouldRentAgainRating = seed.WouldRent,
                Comment = seed.Comment,
                CreatedAt = now.AddDays(-seed.CreatedDaysAgo)
            }, cancellationToken);
            added++;
        }

        if (added > 0)
            _logger.LogInformation("Demo seed: created {Count} review(s).", added);

        return added;
    }

    private readonly record struct SeedBookingRef(Guid ListingId, string RenterEmail);

    private async Task<HashSet<Guid>> ResolvePresentListingIdsAsync(
        Guid[] ids,
        CancellationToken cancellationToken)
    {
        var present = (await _dbContext.Listings
            .Where(listing => ids.Contains(listing.Id))
            .Select(listing => listing.Id)
            .ToListAsync(cancellationToken))
            .ToHashSet();

        foreach (var entry in _dbContext.ChangeTracker.Entries<Listing>())
        {
            if (entry.State == EntityState.Added && ids.Contains(entry.Entity.Id))
            {
                present.Add(entry.Entity.Id);
            }
        }

        return present;
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
