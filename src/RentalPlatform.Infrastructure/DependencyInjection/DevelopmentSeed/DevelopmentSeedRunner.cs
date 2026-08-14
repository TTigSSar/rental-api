using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.DependencyInjection.SeedSupport;
using RentalPlatform.Infrastructure.Persistence;

namespace RentalPlatform.Infrastructure.DependencyInjection.DevelopmentSeed;

/// <summary>
/// Orchestrates development-only database seeding. Every step is idempotent:
/// existing rows (by fixed seed GUIDs or slug/email) are detected and skipped,
/// so repeated startups never duplicate data.
/// </summary>
internal sealed class DevelopmentSeedRunner
{
    // Shared with SeedReportsAsync (the Message-target demo report points at this same thread).
    private static readonly Guid DemoConversationId = new("88888888-0001-4000-9000-000000000001");

    private readonly AppDbContext _dbContext;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ILogger<DevelopmentSeedRunner> _logger;
    private readonly IFileStorageService _fileStorage;
    private readonly HttpClient _http;

    public DevelopmentSeedRunner(
        AppDbContext dbContext,
        IPasswordHasher passwordHasher,
        ILogger<DevelopmentSeedRunner> logger,
        IFileStorageService fileStorage,
        HttpClient http)
    {
        _dbContext = dbContext;
        _passwordHasher = passwordHasher;
        _logger = logger;
        _fileStorage = fileStorage;
        _http = http;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);

        var insertedCategories = await SeedCategoriesAsync(cancellationToken);
        var insertedCategoryKeywords = await SeedCategoryKeywordsAsync(cancellationToken);
        var (userByEmail, insertedUsers) = await SeedUsersAsync(now, cancellationToken);
        var insertedListings = await SeedListingsAsync(userByEmail, now, cancellationToken);
        var insertedImages = await SeedListingImagesAsync(cancellationToken);
        var insertedFavorites = await SeedFavoritesAsync(userByEmail, now, cancellationToken);
        var insertedBookings = await SeedBookingsAsync(userByEmail, today, now, cancellationToken);
        var insertedReviews = await SeedReviewsAsync(userByEmail, now, cancellationToken);
        var insertedChat = await SeedChatAsync(userByEmail, now, cancellationToken);
        var insertedReports = await SeedReportsAsync(userByEmail, now, cancellationToken);

        var totalInserted =
            insertedCategories + insertedCategoryKeywords + insertedUsers + insertedListings +
            insertedImages + insertedFavorites + insertedBookings + insertedReviews + insertedChat +
            insertedReports;

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
                DisplayOrder = category.DisplayOrder,
                ColorHex = category.ColorHex,
                IsVisible = true
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
            // Admin console Phase 2 pastel palette. IsVisible is deliberately NOT force-backfilled
            // here (unlike the fields above): it already defaults true for every pre-existing row
            // via the AddCategoryVisibilityAndColor migration, and stomping it on every seed run
            // would silently undo an admin's manual hide toggle on a dev database.
            if (existing.ColorHex != seed.ColorHex)  { existing.ColorHex     = seed.ColorHex;      dirty = true; }
            if (dirty) updated++;
        }

        return newRows.Length + updated;
    }

    /// <summary>
    /// Admin console Phase 6 ("Needs category fix"): seeds the CategoryKeyword rows in
    /// DevelopmentSeedData.CategoryKeywords. Idempotent by the (CategoryId, Keyword) pair — the
    /// same uniqueness the DB index enforces — rather than a fixed-GUID table like
    /// DevelopmentSeedData.ReportIds, since these rows have no id an application caller ever needs
    /// to reference.
    /// </summary>
    private async Task<int> SeedCategoryKeywordsAsync(CancellationToken cancellationToken)
    {
        var slugs = DevelopmentSeedData.CategoryKeywords
            .Select(keyword => keyword.CategorySlug)
            .Distinct()
            .ToArray();

        var categoriesBySlug = (await _dbContext.Categories
                .Where(category => slugs.Contains(category.Slug))
                .ToListAsync(cancellationToken))
            .ToDictionary(category => category.Slug, category => category, StringComparer.OrdinalIgnoreCase);

        // SeedCategoriesAsync (just above, same RunAsync call) may have added brand-new category
        // rows that aren't persisted yet on a fresh database — the same "Added but not yet saved"
        // situation SeedReportsAsync handles for DemoConversationId. Merge those in too so a
        // first-ever run can still resolve every slug.
        foreach (var entry in _dbContext.ChangeTracker.Entries<Category>())
        {
            if (entry.State == EntityState.Added && !categoriesBySlug.ContainsKey(entry.Entity.Slug))
            {
                categoriesBySlug[entry.Entity.Slug] = entry.Entity;
            }
        }

        var existingPairs = (await _dbContext.CategoryKeywords
                .Select(keyword => new { keyword.CategoryId, keyword.Keyword })
                .ToListAsync(cancellationToken))
            .Select(row => (row.CategoryId, Keyword: row.Keyword.ToLowerInvariant()))
            .ToHashSet();

        var newRows = new List<CategoryKeyword>();
        foreach (var seed in DevelopmentSeedData.CategoryKeywords)
        {
            if (!categoriesBySlug.TryGetValue(seed.CategorySlug, out var category))
            {
                _logger.LogWarning(
                    "Skipping seed category keyword {Keyword}: category slug {Slug} was not found.",
                    seed.Keyword, seed.CategorySlug);
                continue;
            }

            var pairKey = (category.Id, Keyword: seed.Keyword.ToLowerInvariant());
            if (!existingPairs.Add(pairKey))
            {
                continue;
            }

            newRows.Add(new CategoryKeyword
            {
                Id = Guid.NewGuid(),
                CategoryId = category.Id,
                Keyword = seed.Keyword
            });
        }

        if (newRows.Count > 0)
        {
            await _dbContext.CategoryKeywords.AddRangeAsync(newRows, cancellationToken);
        }

        return newRows.Count;
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

        var pending = DevelopmentSeedData.ListingImages
            .Where(image => !existingIdSet.Contains(image.Id) && presentListingIds.Contains(image.ListingId))
            .ToArray();

        var newRows = new List<ListingImage>(pending.Length);
        foreach (var image in pending)
        {
            var url = await ResolveImageUrlAsync(image.Url, image.ListingId, image.FallbackUrl, cancellationToken);
            newRows.Add(new ListingImage
            {
                Id = image.Id,
                ListingId = image.ListingId,
                Url = url,
                IsPrimary = image.IsPrimary,
                SortOrder = image.SortOrder
            });
        }

        if (newRows.Count > 0)
        {
            await _dbContext.ListingImages.AddRangeAsync(newRows, cancellationToken);
        }

        return newRows.Count;
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

            var booking = new Booking
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
            };

            // Lifecycle timestamps so the Booking Details timeline renders for demo data.
            if (seed.Status is BookingStatus.Approved or BookingStatus.Active or BookingStatus.Completed)
            {
                booking.ApprovedAt = createdAt;
            }

            if (seed.Status is BookingStatus.Active or BookingStatus.Completed)
            {
                booking.ActiveAt = startDate.ToDateTime(TimeOnly.MinValue);
            }

            if (seed.Status == BookingStatus.Completed)
            {
                booking.CompletedAt = endDate.ToDateTime(TimeOnly.MinValue);
            }

            if (seed.Status == BookingStatus.Rejected)
            {
                booking.RejectionReason = seed.RejectionReason;
            }

            newRows.Add(booking);
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

    /// <summary>
    /// Seeds ONE demo chat thread so the chat vertical is demoable end-to-end. Idempotent by the
    /// fixed conversation GUID: inserts only when that conversation is absent. The thread hangs off
    /// the seeded Approved booking (LEGO Duplo Starter Set) between owner@ and renter@, with two
    /// participant rows and four alternating text messages. The renter's read cursor is left before
    /// the owner's final message so the renter has exactly one unread and the "Seen" receipt on the
    /// owner's last message reads as not-yet-seen.
    /// </summary>
    private async Task<int> SeedChatAsync(
        IDictionary<string, User> userByEmail,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var conversationId = DemoConversationId;
        var bookingId = new Guid("55555555-0002-4000-9000-000000000002"); // Approved: LEGO Duplo, owner@ ↔ renter@
        var listingId = DevelopmentSeedData.ListingIds.LegoDuploStarterSet;

        // Idempotent: only insert when this fixed conversation is missing.
        var alreadyPresent = await _dbContext.Conversations
            .AnyAsync(conversation => conversation.Id == conversationId, cancellationToken);
        if (alreadyPresent)
        {
            return 0;
        }

        if (!userByEmail.TryGetValue(NormalizeEmail(DevelopmentSeedCredentials.OwnerEmail), out var owner) ||
            !userByEmail.TryGetValue(NormalizeEmail(DevelopmentSeedCredentials.RenterEmail), out var renter))
        {
            _logger.LogWarning("Skipping seed chat: owner or renter demo user is missing.");
            return 0;
        }

        // The Approved booking backing this thread must exist (in the DB or queued in this same run).
        var bookingPresent = await _dbContext.Bookings.AnyAsync(booking => booking.Id == bookingId, cancellationToken)
            || _dbContext.ChangeTracker.Entries<Booking>()
                .Any(entry => entry.State == EntityState.Added && entry.Entity.Id == bookingId);
        if (!bookingPresent)
        {
            _logger.LogWarning("Skipping seed chat: approved booking {BookingId} is missing.", bookingId);
            return 0;
        }

        var toyTitle = DevelopmentSeedData.Listings.First(listing => listing.Id == listingId).Title;
        var toyImageUrl = await ResolveSeedPrimaryImageUrlAsync(listingId, cancellationToken);

        var conversation = new Conversation
        {
            Id = conversationId,
            BookingId = bookingId,
            OwnerId = owner.Id,
            RenterId = renter.Id,
            ToyTitle = toyTitle,
            ToyImageUrl = toyImageUrl,
            ClosedAt = null,
            CreatedAt = now.AddMinutes(-45)
        };

        // Four alternating text messages, oldest → newest. The LAST message is from the owner and
        // is left unread by the renter (see read cursors below).
        var messages = new[]
        {
            new ChatMessage
            {
                Id = new Guid("88888888-0003-4000-9000-000000000001"),
                ConversationId = conversationId,
                SenderId = renter.Id,
                Type = MessageType.Text,
                Body = "Hi! Is the LEGO Duplo set still available for those dates?",
                CreatedAt = now.AddMinutes(-40)
            },
            new ChatMessage
            {
                Id = new Guid("88888888-0003-4000-9000-000000000002"),
                ConversationId = conversationId,
                SenderId = owner.Id,
                Type = MessageType.Text,
                Body = "Yes, it's set aside for you and freshly sanitized.",
                CreatedAt = now.AddMinutes(-30)
            },
            new ChatMessage
            {
                Id = new Guid("88888888-0003-4000-9000-000000000003"),
                ConversationId = conversationId,
                SenderId = renter.Id,
                Type = MessageType.Text,
                Body = "Perfect, thank you! Where should we meet for pickup?",
                CreatedAt = now.AddMinutes(-20)
            },
            new ChatMessage
            {
                Id = new Guid("88888888-0003-4000-9000-000000000004"),
                ConversationId = conversationId,
                SenderId = owner.Id,
                Type = MessageType.Text,
                Body = "Let's meet at 8 Saryan St around 6pm. See you then!",
                CreatedAt = now.AddMinutes(-10)
            }
        };

        var lastMessage = messages[^1];
        var renterLastRead = messages[^2]; // renter has read up to the message before the owner's final one

        // Denormalised inbox preview from the newest message.
        conversation.LastMessageId = lastMessage.Id;
        conversation.LastMessageSnippet = lastMessage.Body;
        conversation.LastMessageAt = lastMessage.CreatedAt;

        var participants = new[]
        {
            // Owner has read everything ⇒ 0 unread; their final message shows as not-yet-seen by the renter.
            new ConversationParticipant
            {
                Id = new Guid("88888888-0002-4000-9000-000000000001"),
                ConversationId = conversationId,
                UserId = owner.Id,
                LastReadMessageId = lastMessage.Id,
                LastReadAt = now
            },
            // Renter has read up to the second-to-last message ⇒ exactly 1 unread (the owner's final message).
            new ConversationParticipant
            {
                Id = new Guid("88888888-0002-4000-9000-000000000002"),
                ConversationId = conversationId,
                UserId = renter.Id,
                LastReadMessageId = renterLastRead.Id,
                LastReadAt = renterLastRead.CreatedAt
            }
        };

        await _dbContext.Conversations.AddAsync(conversation, cancellationToken);
        await _dbContext.ChatMessages.AddRangeAsync(messages, cancellationToken);
        await _dbContext.ConversationParticipants.AddRangeAsync(participants, cancellationToken);

        _logger.LogInformation(
            "Demo seed: created 1 chat conversation with {MessageCount} messages (renter has 1 unread).",
            messages.Length);

        return 1 + messages.Length + participants.Length;
    }

    /// <summary>
    /// Seeds a handful of demo reports spanning every ReportSeverity (Low/Medium/High) and every
    /// ReportStatus (Open/Resolved/Dismissed), against a mix of listing/user/message targets, with
    /// realistic reporter attribution — so the admin Reports screen and the FlagCount/
    /// OwnerOpenReportCount aggregates have real data in dev without any mock code path. Bespoke,
    /// hand-written (not a declarative SeedReport[] table) because each row's target is a
    /// different polymorphic kind — same convention as SeedChatAsync. Idempotent by the fixed
    /// DevelopmentSeedData.ReportIds GUIDs: each row inserts only when its id is absent.
    /// Severity is derived from ReasonCode via ReportReasonCatalog, exactly like production
    /// (ReportsService.CreateAsync) — never hardcoded here — so the two can never drift apart.
    /// </summary>
    private async Task<int> SeedReportsAsync(
        IDictionary<string, User> userByEmail,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var reportIds = new[]
        {
            DevelopmentSeedData.ReportIds.UnsafeBalanceBike,
            DevelopmentSeedData.ReportIds.MisleadingKitchenPhotos,
            DevelopmentSeedData.ReportIds.RenterNoShow,
            DevelopmentSeedData.ReportIds.RudeMessagesResolved,
            DevelopmentSeedData.ReportIds.OffPlatformPaymentDismissed,
            DevelopmentSeedData.ReportIds.SpamListing,
            DevelopmentSeedData.ReportIds.OtherOwnerFlag
        };

        var existingIds = (await _dbContext.Reports
            .Where(report => reportIds.Contains(report.Id))
            .Select(report => report.Id)
            .ToListAsync(cancellationToken))
            .ToHashSet();

        if (existingIds.Count == reportIds.Length)
        {
            return 0;
        }

        User? UserOrNull(string email) =>
            userByEmail.TryGetValue(NormalizeEmail(email), out var user) ? user : null;

        var renter = UserOrNull(DevelopmentSeedCredentials.RenterEmail);
        var owner = UserOrNull(DevelopmentSeedCredentials.OwnerEmail);
        var secondUser = UserOrNull(DevelopmentSeedCredentials.SecondUserEmail);
        var admin = UserOrNull(DevelopmentSeedCredentials.AdminEmail);
        var demoOwner = UserOrNull(DevelopmentSeedCredentials.DemoOwnerEmail);
        var demoRenter = UserOrNull(DevelopmentSeedCredentials.DemoRenterEmail);
        var davit = UserOrNull(DevelopmentSeedCredentials.OwnerDavitEmail);
        var anahit = UserOrNull(DevelopmentSeedCredentials.OwnerAnahitEmail);

        if (renter is null || owner is null || secondUser is null || admin is null
            || demoOwner is null || demoRenter is null || davit is null || anahit is null)
        {
            _logger.LogWarning("Skipping seed reports: one or more demo users are missing.");
            return 0;
        }

        // The Message-target report points at the owner@/renter@ demo thread seeded by
        // SeedChatAsync. If that thread hasn't been created yet (fresh DB, this same run), it is
        // queued as Added on the change tracker — present() checks both cases.
        var conversationPresent = await _dbContext.Conversations
            .AnyAsync(conversation => conversation.Id == DemoConversationId, cancellationToken)
            || _dbContext.ChangeTracker.Entries<Conversation>()
                .Any(entry => entry.State == EntityState.Added && entry.Entity.Id == DemoConversationId);

        string TitleOf(Guid listingId) =>
            DevelopmentSeedData.Listings.First(listing => listing.Id == listingId).Title;

        var candidates = new List<Report>();

        void AddIfMissing(Report report)
        {
            if (!existingIds.Contains(report.Id))
            {
                candidates.Add(report);
            }
        }

        Report BuildReport(
            Guid id, ReportTargetType targetType, Guid targetId, string targetLabel,
            Guid reporterId, string reasonCode, string? detail, ReportStatus status, DateTime createdAt,
            Guid? resolvedByUserId = null, string? resolutionNote = null, DateTime? resolvedAt = null) => new()
        {
            Id = id,
            TargetType = targetType,
            TargetId = targetId,
            TargetLabel = targetLabel,
            ReporterUserId = reporterId,
            ReasonCode = reasonCode,
            Detail = detail,
            Severity = ReportReasonCatalog.SeverityFor(reasonCode),
            Status = status,
            CreatedAt = createdAt,
            ResolvedAt = resolvedAt,
            ResolvedByUserId = resolvedByUserId,
            ResolutionNote = resolutionNote
        };

        // Open, High — listing, unsafe item.
        AddIfMissing(BuildReport(
            DevelopmentSeedData.ReportIds.UnsafeBalanceBike,
            ReportTargetType.Listing, DevelopmentSeedData.ListingIds.KidsBalanceBike,
            TitleOf(DevelopmentSeedData.ListingIds.KidsBalanceBike),
            renter.Id, "unsafeItem",
            "The balance bike's frame has a visible crack near the front fork. Feels unsafe to ride.",
            ReportStatus.Open, now.AddHours(-6)));

        // Open, Medium — listing, misleading photos.
        AddIfMissing(BuildReport(
            DevelopmentSeedData.ReportIds.MisleadingKitchenPhotos,
            ReportTargetType.Listing, DevelopmentSeedData.ListingIds.ToyKitchenSet,
            TitleOf(DevelopmentSeedData.ListingIds.ToyKitchenSet),
            secondUser.Id, "misleadingPhotos",
            "Photos show a full accessory set but three of the play-food pieces were missing at pickup.",
            ReportStatus.Open, now.AddDays(-1)));

        // Open, High — user, no-show at pickup.
        AddIfMissing(BuildReport(
            DevelopmentSeedData.ReportIds.RenterNoShow,
            ReportTargetType.User, renter.Id, $"{renter.FirstName} {renter.LastName}".Trim(),
            owner.Id, "noShow",
            "Confirmed a 6pm pickup and never showed up or replied to messages.",
            ReportStatus.Open, now.AddHours(-3)));

        // Resolved, Low — user, rude messages, already actioned by admin@.
        AddIfMissing(BuildReport(
            DevelopmentSeedData.ReportIds.RudeMessagesResolved,
            ReportTargetType.User, secondUser.Id, $"{secondUser.FirstName} {secondUser.LastName}".Trim(),
            renter.Id, "rudeMessages",
            "Sent several insulting messages after a booking request was declined.",
            ReportStatus.Resolved, now.AddDays(-4),
            resolvedByUserId: admin.Id,
            resolutionNote: "Reviewed the chat log and issued the user a warning.",
            resolvedAt: now.AddDays(-3)));

        // Dismissed, Medium — message, off-platform payment attempt, found to be unfounded.
        if (conversationPresent)
        {
            AddIfMissing(BuildReport(
                DevelopmentSeedData.ReportIds.OffPlatformPaymentDismissed,
                ReportTargetType.Message, DemoConversationId, $"Chat with {owner.FirstName} {owner.LastName}".Trim(),
                renter.Id, "offPlatformPayment",
                "Thought the owner was steering the conversation toward a bank transfer outside the app.",
                ReportStatus.Dismissed, now.AddDays(-2),
                resolvedByUserId: admin.Id,
                resolutionNote: "No evidence of an off-platform payment attempt in the thread.",
                resolvedAt: now.AddDays(-1)));
        }

        // Open, Medium — listing, spam/scam, catalogue-expansion owner.
        AddIfMissing(BuildReport(
            DevelopmentSeedData.ReportIds.SpamListing,
            ReportTargetType.Listing, DevelopmentSeedData.ListingIds.LEGOClassicCreativeBricksBox500Pcs,
            TitleOf(DevelopmentSeedData.ListingIds.LEGOClassicCreativeBricksBox500Pcs),
            davit.Id, "spam",
            "Listing text looks copy-pasted from an unrelated resale site, unsure it's a real rental.",
            ReportStatus.Open, now.AddDays(-5)));

        // Open, Low — user, other, catalogue-expansion demo pair.
        AddIfMissing(BuildReport(
            DevelopmentSeedData.ReportIds.OtherOwnerFlag,
            ReportTargetType.User, demoOwner.Id, $"{demoOwner.FirstName} {demoOwner.LastName}".Trim(),
            demoRenter.Id, "other",
            "Asked me to pay a deposit by bank transfer before confirming the booking.",
            ReportStatus.Open, now.AddHours(-20)));

        if (candidates.Count > 0)
        {
            await _dbContext.Reports.AddRangeAsync(candidates, cancellationToken);
            _logger.LogInformation("Demo seed: created {Count} report(s).", candidates.Count);
        }

        return candidates.Count;
    }

    private async Task<string?> ResolveSeedPrimaryImageUrlAsync(Guid listingId, CancellationToken cancellationToken)
    {
        var url = await _dbContext.ListingImages
            .Where(image => image.ListingId == listingId && image.IsPrimary)
            .OrderBy(image => image.SortOrder)
            .Select(image => image.Url)
            .FirstOrDefaultAsync(cancellationToken);

        if (url is not null)
        {
            return url;
        }

        // On a fresh database the image is queued for insert in this same run (not yet persisted).
        return _dbContext.ChangeTracker.Entries<ListingImage>()
            .Where(entry => entry.State == EntityState.Added
                && entry.Entity.ListingId == listingId
                && entry.Entity.IsPrimary)
            .OrderBy(entry => entry.Entity.SortOrder)
            .Select(entry => entry.Entity.Url)
            .FirstOrDefault();
    }

    private Task<string> ResolveImageUrlAsync(
        string sourceUrl, Guid listingId, string fallbackUrl, CancellationToken cancellationToken) =>
        SeedImageResolver.ResolveImageUrlAsync(_http, _fileStorage, _logger, sourceUrl, listingId, fallbackUrl, cancellationToken);

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
