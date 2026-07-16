using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.DependencyInjection.DevelopmentSeed;
using RentalPlatform.Infrastructure.DependencyInjection.SeedSupport;
using RentalPlatform.Infrastructure.Persistence;

namespace RentalPlatform.Infrastructure.DependencyInjection.DemoContentBootstrap;

/// <summary>
/// Idempotently seeds an initial public catalogue (a showcase owner + their Approved listings and
/// images) on startup in environments with no Development seed — i.e. Production — so the live site
/// is never left showing an empty marketplace. Structural sibling of
/// <see cref="RentalPlatform.Infrastructure.DependencyInjection.AdminBootstrap.AdminBootstrapRunner"/>,
/// but reuses the listing/image content from <see cref="DevelopmentSeedData"/> instead of duplicating
/// it — the toy-catalogue data has exactly one source of truth.
///
/// Driven entirely by configuration:
///   Bootstrap:DemoContentEnabled  — must be true, or this is a silent no-op
///   Bootstrap:DemoOwnerEmail      — login email for the showcase owner account
///   Bootstrap:DemoOwnerPassword   — the owner's initial password (BCrypt-hashed before storage)
///
/// Only listings whose seed status is Approved are created — public endpoints expose Approved
/// listings only, so Draft/PendingApproval/Rejected demo variants have no business being pushed to
/// a live site. No other accounts (no admin/renter demo users) and no bookings/reviews/chat are
/// created here — those stay Development-only.
///
/// Additive only: every check is "does this row already exist?" — if so, skip it untouched. Unlike
/// the Development seed, this runner never updates a row it finds already present, because on
/// Production that row might by then be something a real user (the showcase owner themself, via the
/// normal owner UI) has since edited.
/// </summary>
internal sealed class DemoContentBootstrapRunner
{
    private readonly AppDbContext _dbContext;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IFileStorageService _fileStorage;
    private readonly HttpClient _http;
    private readonly ILogger<DemoContentBootstrapRunner> _logger;

    public DemoContentBootstrapRunner(
        AppDbContext dbContext,
        IPasswordHasher passwordHasher,
        IFileStorageService fileStorage,
        HttpClient http,
        ILogger<DemoContentBootstrapRunner> logger)
    {
        _dbContext = dbContext;
        _passwordHasher = passwordHasher;
        _fileStorage = fileStorage;
        _http = http;
        _logger = logger;
    }

    public async Task RunAsync(
        bool enabled,
        string? ownerEmail,
        string? ownerPassword,
        CancellationToken cancellationToken)
    {
        // Not enabled, or the owner credentials are incomplete — nothing to do. No log line by
        // design, mirroring AdminBootstrapRunner: this is the expected, silent steady state on any
        // environment that doesn't opt in (or hasn't finished configuring the owner account yet).
        if (!enabled || string.IsNullOrWhiteSpace(ownerEmail) || string.IsNullOrWhiteSpace(ownerPassword))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var normalizedEmail = NormalizeEmail(ownerEmail);

        var owner = await _dbContext.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail, cancellationToken);
        var ownerCreated = false;
        if (owner is null)
        {
            owner = new User
            {
                Id = Guid.NewGuid(),
                Email = normalizedEmail,
                // Hashed exactly like every other account (see AuthService.RegisterAsync /
                // AdminBootstrapRunner) — never log the raw password.
                PasswordHash = _passwordHasher.HashPassword(ownerPassword),
                FirstName = "DoRent",
                LastName = "Showcase",
                // Placeholder only — never a working number. Same shape as the seeded demo owners
                // (+374 <2-digit> <3-digit> <3-digit>) so the contact-reveal UI renders it identically.
                PhoneNumber = "+374 99 000 000",
                PreferredLanguage = "en",
                ExternalAuthProvider = null,
                ExternalProviderId = null,
                AvatarUrl = null,
                CreatedAt = now,
                IsBlocked = false,
                Role = UserRole.User
            };

            await _dbContext.Users.AddAsync(owner, cancellationToken);
            ownerCreated = true;
        }

        var categoriesBySlug = await _dbContext.Categories
            .ToDictionaryAsync(category => category.Slug, category => category, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var approvedSeeds = DevelopmentSeedData.Listings
            .Where(listing => listing.Status == ListingStatus.Approved)
            .ToArray();

        var seedListingIds = approvedSeeds.Select(listing => listing.Id).ToArray();
        var existingListingIds = (await _dbContext.Listings
            .Where(listing => seedListingIds.Contains(listing.Id))
            .Select(listing => listing.Id)
            .ToListAsync(cancellationToken))
            .ToHashSet();

        var newListings = new List<Listing>();
        foreach (var seed in approvedSeeds)
        {
            if (existingListingIds.Contains(seed.Id))
            {
                continue;
            }

            if (!categoriesBySlug.TryGetValue(seed.CategorySlug, out var category))
            {
                _logger.LogWarning(
                    "Demo content bootstrap: skipping listing '{Title}' — category '{Slug}' is missing.",
                    seed.Title, seed.CategorySlug);
                continue;
            }

            newListings.Add(new Listing
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
                Status = ListingStatus.Approved,
                CreatedAt = now.AddDays(-seed.CreatedDaysAgo),
                UpdatedAt = now.AddDays(-seed.UpdatedDaysAgo)
            });
        }

        if (newListings.Count > 0)
        {
            await _dbContext.Listings.AddRangeAsync(newListings, cancellationToken);
        }

        // A listing is "present" for image-attachment purposes if it already existed, or if it was
        // just queued for insert above (approved-only, so anything outside that set is intentionally
        // excluded — no images for Draft/Pending/Rejected demo listings on Production).
        var presentListingIds = existingListingIds;
        foreach (var listing in newListings)
        {
            presentListingIds.Add(listing.Id);
        }

        var seedImages = DevelopmentSeedData.ListingImages
            .Where(image => presentListingIds.Contains(image.ListingId))
            .ToArray();

        var seedImageIds = seedImages.Select(image => image.Id).ToArray();
        var existingImageIds = (await _dbContext.ListingImages
            .Where(image => seedImageIds.Contains(image.Id))
            .Select(image => image.Id)
            .ToListAsync(cancellationToken))
            .ToHashSet();

        var newImages = new List<ListingImage>();
        foreach (var image in seedImages)
        {
            if (existingImageIds.Contains(image.Id))
            {
                continue;
            }

            var url = await SeedImageResolver.ResolveImageUrlAsync(
                _http, _fileStorage, _logger, image.Url, image.ListingId, image.FallbackUrl, cancellationToken);

            newImages.Add(new ListingImage
            {
                Id = image.Id,
                ListingId = image.ListingId,
                Url = url,
                IsPrimary = image.IsPrimary,
                SortOrder = image.SortOrder
            });
        }

        if (newImages.Count > 0)
        {
            await _dbContext.ListingImages.AddRangeAsync(newImages, cancellationToken);
        }

        if (!ownerCreated && newListings.Count == 0 && newImages.Count == 0)
        {
            _logger.LogInformation("Demo content bootstrap: already present, skipping.");
            return;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Demo content bootstrap completed. Owner created: {OwnerCreated}, listings: {Listings}, images: {Images}.",
            ownerCreated, newListings.Count, newImages.Count);
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
