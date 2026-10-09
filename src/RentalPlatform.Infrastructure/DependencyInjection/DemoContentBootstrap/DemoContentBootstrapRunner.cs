using System.Security.Cryptography;
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
/// Idempotently seeds an initial public catalogue (showcase owners + their Approved listings and
/// images) on startup in environments with no Development seed — i.e. Production — so the live site
/// is never left showing an empty marketplace. Structural sibling of
/// <see cref="RentalPlatform.Infrastructure.DependencyInjection.AdminBootstrap.AdminBootstrapRunner"/>,
/// but reuses the listing/image content from <see cref="DevelopmentSeedData"/> instead of duplicating
/// it — the toy-catalogue data has exactly one source of truth.
///
/// <para>
/// <b>Why there is now an owner per district.</b> A listing's location is its OWNER's home point
/// (home-point model). A single showcase owner would therefore put the entire public catalogue on a
/// single pin, and the catalogue map — the thing this bootstrap exists to populate — would render as
/// one marker. So the showcase is spread across up to 12 owners, one per Yerevan district, each with
/// a deterministic email derived from the configured base address and a home point from
/// <see cref="DevelopmentSeedData.HomePointByDistrictCode"/>.
/// </para>
///
/// <para>
/// <b>Which district a listing belongs to</b> is answered in this order:
/// (1) the coordinates it had BEFORE the AddUserHomePoint migration, from the
/// ListingLocationsBeforeHomePoint snapshot — the real, pre-collapse answer for a catalogue that is
/// already live; (2) failing that, the district of the seed owner the listing is declared under in
/// DevelopmentSeedData, which is how a listing this bootstrap has never created before is placed;
/// (3) failing both, it stays with the base owner and a warning is logged. Nothing is ever guessed.
/// </para>
///
/// Driven entirely by configuration:
///   Bootstrap:DemoContentEnabled  — must be true, or this is a silent no-op
///   Bootstrap:DemoOwnerEmail      — base login email; per-district accounts derive from it
///   Bootstrap:DemoOwnerPassword   — the BASE account's initial password, and ONLY its own
///                                   (BCrypt-hashed before storage)
///
/// <para>
/// <b>Only the base account is a login.</b> The per-district accounts exist for exactly one
/// purpose — to OWN listings, so the catalogue map has more than one pin — and nobody signs in as
/// them. They are therefore created with a per-account cryptographically random password that is
/// hashed and then dropped: it is never logged, never persisted in plaintext and never reported
/// anywhere, so there is no credential to leak and no password to look up. The configured
/// <c>Bootstrap:DemoOwnerPassword</c> is applied to the base account alone, which stays exactly the
/// login the operator configured (ADR-005's knowingly-accepted trade-off covers that ONE account;
/// handing the same published password to thirteen live accounts would widen it silently).
/// Operating on a district account, should that ever be wanted, means a password reset for it —
/// not a value in the server <c>.env</c>.
/// </para>
///
/// Only listings whose seed status is Approved are created — public endpoints expose Approved
/// listings only, so Draft/PendingApproval/Rejected demo variants have no business being pushed to
/// a live site. No other accounts (no admin/renter demo users) and no bookings/reviews/chat are
/// created here — those stay Development-only.
///
/// <para>
/// Additive for content: a listing row that already exists is never re-created and its content is
/// never edited, because on Production it may by now be something a real user has edited. The ONE
/// thing this runner does change on an existing row is its OWNER, and only to redistribute the
/// showcase across districts — and never for a listing that has a booking, since that would hand a
/// stranger someone else's rental and re-point a live handover.
/// </para>
/// </summary>
internal sealed class DemoContentBootstrapRunner
{
    private readonly AppDbContext _dbContext;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IFileStorageService _fileStorage;
    private readonly HttpClient _http;
    private readonly ILogger<DemoContentBootstrapRunner> _logger;
    private readonly IDistrictBoundaryProvider _districtBoundaryProvider;
    private readonly IHomePointService _homePointService;

    public DemoContentBootstrapRunner(
        AppDbContext dbContext,
        IPasswordHasher passwordHasher,
        IFileStorageService fileStorage,
        HttpClient http,
        ILogger<DemoContentBootstrapRunner> logger,
        IDistrictBoundaryProvider districtBoundaryProvider,
        IHomePointService homePointService)
    {
        _dbContext = dbContext;
        _passwordHasher = passwordHasher;
        _fileStorage = fileStorage;
        _http = http;
        _logger = logger;
        _districtBoundaryProvider = districtBoundaryProvider;
        _homePointService = homePointService;
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
        var baseEmail = NormalizeEmail(ownerEmail);

        var approvedSeeds = DevelopmentSeedData.Listings
            .Where(listing => listing.Status == ListingStatus.Approved)
            .ToArray();

        var placementByListingId = await ResolveDistrictsAsync(approvedSeeds, cancellationToken);

        // The base account still exists and still owns anything unplaceable, so an operator's
        // configured login always works and no listing is ever left ownerless. This is the ONE
        // account the configured password belongs to.
        var baseOwner = await EnsureOwnerAsync(
            baseEmail, "DoRent", "Showcase", () => ownerPassword, now, cancellationToken);

        var ownersByBucket = new Dictionary<string, User>(StringComparer.OrdinalIgnoreCase);
        foreach (var bucket in placementByListingId.Values.Where(p => p.IsKnown).Select(p => p.Bucket).Distinct())
        {
            var owner = await EnsureOwnerAsync(
                BuildDistrictOwnerEmail(baseEmail, bucket),
                "DoRent",
                $"Showcase ({bucket})",
                // Not a login — a random secret per account, hashed and discarded. See the class
                // remarks for why the configured password stops at the base account.
                GenerateUnusableRandomPassword,
                now,
                cancellationToken);

            ownersByBucket[bucket] = owner;
        }

        var createdListings = await CreateMissingListingsAsync(
            approvedSeeds, placementByListingId, ownersByBucket, baseOwner, now, cancellationToken);

        var createdImages = await CreateMissingImagesAsync(approvedSeeds, cancellationToken);

        // Persist owners/listings/images before the two reconciliation steps: both query the
        // database, so they must see everything above as committed rows, not tracked changes.
        await _dbContext.SaveChangesAsync(cancellationToken);

        var redistributed = await RedistributeExistingListingsAsync(
            placementByListingId, ownersByBucket, cancellationToken);

        // Last: give every showcase owner their home point, which moves all of their listings onto
        // it in one go. Done through the real single writer, never re-implemented here.
        var homePointsApplied = await ApplyOwnerHomePointsAsync(ownersByBucket, cancellationToken);

        if (createdListings == 0 && createdImages == 0 && redistributed == 0 && homePointsApplied == 0)
        {
            _logger.LogInformation("Demo content bootstrap: already present, skipping.");
            return;
        }

        _logger.LogInformation(
            "Demo content bootstrap completed. Showcase owners: {Owners}, listings created: {Listings}, images: {Images}, listings redistributed by district: {Redistributed}, home points applied: {HomePoints}.",
            ownersByBucket.Count, createdListings, createdImages, redistributed, homePointsApplied);
    }

    /// <summary>
    /// Where one showcase listing belongs: <see cref="Bucket"/> is a Yerevan district code.
    /// <see cref="IsKnown"/> = false means "no district could be established", which is the case
    /// that keeps the base owner and logs a warning.
    /// </summary>
    /// <remarks>
    /// There is no "outside Yerevan" bucket. A showcase owner needs a home point, and a home point
    /// outside Yerevan cannot be saved — so a pre-migration coordinate that resolves to no district
    /// is treated as "this snapshot cannot place the listing" and the seed owner's district is used
    /// instead, rather than inventing an owner who could not exist.
    /// </remarks>
    private readonly record struct DistrictPlacement(string Bucket, bool IsKnown)
    {
        public static DistrictPlacement Unknown => new(string.Empty, false);

        public static DistrictPlacement For(string districtCode) => new(districtCode, true);
    }

    /// <summary>
    /// The district each showcase listing should end up in. See the class remarks for the order of
    /// precedence; a null value means "could not be established", not "outside Yerevan".
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, DistrictPlacement>> ResolveDistrictsAsync(
        IReadOnlyCollection<DevelopmentSeedData.SeedListing> approvedSeeds,
        CancellationToken cancellationToken)
    {
        var listingIds = approvedSeeds.Select(listing => listing.Id).ToArray();

        // Source 1: what the listing's coordinates were before the migration collapsed them. A
        // snapshot that resolves to no district is an ANSWER ("outside Yerevan"), not a miss — the
        // coordinates were known, they simply were not in the city.
        var snapshots = await _dbContext.ListingLocationsBeforeHomePoint
            .Where(snapshot => listingIds.Contains(snapshot.ListingId))
            .ToDictionaryAsync(snapshot => snapshot.ListingId, cancellationToken);

        var resolved = new Dictionary<Guid, DistrictPlacement>();
        foreach (var seed in approvedSeeds)
        {
            if (snapshots.TryGetValue(seed.Id, out var snapshot) &&
                snapshot.Latitude is { } latitude &&
                snapshot.Longitude is { } longitude &&
                _districtBoundaryProvider.FindDistrictCode((double)latitude, (double)longitude) is { } snapshotDistrict)
            {
                resolved[seed.Id] = DistrictPlacement.For(snapshotDistrict);
                continue;
            }

            // Source 2: the district of the seed owner this listing is declared under. Listings no
            // longer carry coordinates in the seed data, so the owner IS the seed's statement of
            // where a listing belongs. This also catches a snapshot whose coordinates fall outside
            // Yerevan — on a live database that is a listing from before the Yerevan-only rule, and
            // it has to land somewhere a showcase owner can actually live.
            if (DevelopmentSeedData.ExpectedOwnerHomeDistrictCodes.TryGetValue(seed.OwnerEmail, out var seedOwnerDistrict))
            {
                resolved[seed.Id] = DistrictPlacement.For(seedOwnerDistrict);
                continue;
            }

            _logger.LogWarning(
                "Demo content bootstrap: no district could be established for listing '{Title}' ({ListingId}) — it stays with the base showcase owner.",
                seed.Title, seed.Id);
            resolved[seed.Id] = DistrictPlacement.Unknown;
        }

        return resolved;
    }

    /// <summary>Finds or creates one showcase account, hashing the password exactly like every other account.</summary>
    /// <param name="passwordFactory">
    /// Produces the raw password, and is invoked ONLY when the account is actually being created —
    /// so a steady-state boot generates no secrets at all, and a random one is never produced for
    /// an account that already has a hash.
    /// </param>
    private async Task<User> EnsureOwnerAsync(
        string email,
        string firstName,
        string lastName,
        Func<string> passwordFactory,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var existing = await _dbContext.Users.FirstOrDefaultAsync(user => user.Email == email, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var owner = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            // Hashed exactly like every other account (see AuthService.RegisterAsync /
            // AdminBootstrapRunner) — never log the raw password, and never keep it: the only thing
            // that leaves this expression is the hash.
            PasswordHash = _passwordHasher.HashPassword(passwordFactory()),
            FirstName = firstName,
            LastName = lastName,
            // Placeholder only — never a working number. Same shape as the seeded demo owners
            // (+374 <2-digit> <3-digit> <3-digit>) so the contact-reveal UI renders it identically.
            PhoneNumber = "+374 99 000 000",
            PreferredLanguage = "en",
            ExternalAuthProvider = null,
            ExternalProviderId = null,
            AvatarUrl = null,
            CreatedAt = now,
            IsBlocked = false,
            // Seeded showcase owners never sign up through the public path (ADR-028 §11).
            IsEmailConfirmed = true,
            Role = UserRole.User
        };

        await _dbContext.Users.AddAsync(owner, cancellationToken);
        return owner;
    }

    private async Task<int> CreateMissingListingsAsync(
        IReadOnlyCollection<DevelopmentSeedData.SeedListing> approvedSeeds,
        IReadOnlyDictionary<Guid, DistrictPlacement> placementByListingId,
        IReadOnlyDictionary<string, User> ownersByBucket,
        User baseOwner,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var categoriesBySlug = await _dbContext.Categories
            .ToDictionaryAsync(category => category.Slug, category => category, StringComparer.OrdinalIgnoreCase, cancellationToken);

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

            var placement = placementByListingId[seed.Id];
            var owner = placement.IsKnown && ownersByBucket.TryGetValue(placement.Bucket, out var districtOwner)
                ? districtOwner
                : baseOwner;

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
                // No coordinates: the row inherits its owner's home point, applied below.
                AgeFromMonths = seed.AgeFromMonths,
                AgeToMonths = seed.AgeToMonths,
                Condition = seed.Condition,
                HygieneNotes = seed.HygieneNotes,
                SafetyNotes = seed.SafetyNotes,
                CompensationAmount = seed.CompensationAmount,
                Status = ListingStatus.Approved,
                CreatedAt = now.AddDays(-seed.CreatedDaysAgo),
                UpdatedAt = now.AddDays(-seed.UpdatedDaysAgo)
            });
        }

        if (newListings.Count > 0)
        {
            await _dbContext.Listings.AddRangeAsync(newListings, cancellationToken);
        }

        return newListings.Count;
    }

    private async Task<int> CreateMissingImagesAsync(
        IReadOnlyCollection<DevelopmentSeedData.SeedListing> approvedSeeds,
        CancellationToken cancellationToken)
    {
        // A listing is "present" for image-attachment purposes if it already existed, or if it was
        // just queued for insert above (approved-only, so anything outside that set is intentionally
        // excluded — no images for Draft/Pending/Rejected demo listings on Production).
        var approvedIds = approvedSeeds.Select(listing => listing.Id).ToHashSet();
        var presentListingIds = (await _dbContext.Listings
            .Where(listing => approvedIds.Contains(listing.Id))
            .Select(listing => listing.Id)
            .ToListAsync(cancellationToken))
            .ToHashSet();

        foreach (var entry in _dbContext.ChangeTracker.Entries<Listing>())
        {
            if (entry.State == EntityState.Added && approvedIds.Contains(entry.Entity.Id))
            {
                presentListingIds.Add(entry.Entity.Id);
            }
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

        return newImages.Count;
    }

    /// <summary>
    /// Moves showcase listings that already exist onto the owner for their district. This is the
    /// step that un-collapses a live catalogue the AddUserHomePoint migration stacked onto one pin.
    /// </summary>
    /// <remarks>
    /// A listing with any booking is left alone and logged — on Production those bookings belong to
    /// real people, and the owner is the counterparty they are dealing with.
    /// </remarks>
    private async Task<int> RedistributeExistingListingsAsync(
        IReadOnlyDictionary<Guid, DistrictPlacement> placementByListingId,
        IReadOnlyDictionary<string, User> ownersByBucket,
        CancellationToken cancellationToken)
    {
        var listingIds = placementByListingId.Keys.ToArray();
        var listings = await _dbContext.Listings
            .Where(listing => listingIds.Contains(listing.Id))
            .ToListAsync(cancellationToken);

        var moved = 0;
        foreach (var listing in listings)
        {
            var placement = placementByListingId[listing.Id];
            if (!placement.IsKnown ||
                !ownersByBucket.TryGetValue(placement.Bucket, out var owner) ||
                listing.OwnerId == owner.Id)
            {
                continue;
            }

            if (await _dbContext.Bookings.AnyAsync(booking => booking.ListingId == listing.Id, cancellationToken))
            {
                _logger.LogWarning(
                    "Demo content bootstrap: listing '{Title}' ({ListingId}) belongs in {District} but has bookings — leaving its owner unchanged.",
                    listing.Title, listing.Id, placement.Bucket);
                continue;
            }

            listing.OwnerId = owner.Id;
            moved++;
        }

        if (moved > 0)
        {
            // Committed before the home-point pass so its fan-out sees the new ownership.
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return moved;
    }

    /// <summary>
    /// Sets each showcase owner's home point through the real single writer, which moves every
    /// listing they own onto it. Idempotent: an owner already on that point is a no-op.
    /// </summary>
    private async Task<int> ApplyOwnerHomePointsAsync(
        IReadOnlyDictionary<string, User> ownersByBucket,
        CancellationToken cancellationToken)
    {
        var applied = 0;
        foreach (var (districtCode, owner) in ownersByBucket)
        {
            if (!DevelopmentSeedData.HomePointByDistrictCode.TryGetValue(districtCode, out var point))
            {
                _logger.LogWarning(
                    "Demo content bootstrap: no home point is defined for district '{District}' — its showcase owner has none.",
                    districtCode);
                continue;
            }

            var result = await _homePointService.SetHomePointAsync(
                owner.Id, point.Latitude, point.Longitude, cancellationToken);

            if (!result.IsSuccess)
            {
                _logger.LogWarning(
                    "Demo content bootstrap: could not set the home point for the {District} showcase owner — {ErrorCode}.",
                    districtCode, result.Error?.Code);
                continue;
            }

            if (result.Value)
            {
                applied++;
            }
        }

        return applied;
    }

    /// <summary>
    /// Derives a district account's address from the configured base one:
    /// <c>showcase@dorent.am</c> + <c>kentron</c> becomes <c>showcase.kentron@dorent.am</c>.
    /// </summary>
    /// <remarks>
    /// Deterministic on purpose — it is the idempotency key. This runner finds its accounts by
    /// email, so the same base address must always produce the same district addresses, on every
    /// boot and in every environment. A base address with no "@" is used as the local part with no
    /// domain rather than throwing: this runs on the startup path, and a cosmetic feature must
    /// never take the site down (see ParseDemoContentEnabled for the same principle).
    /// </remarks>
    internal static string BuildDistrictOwnerEmail(string baseEmail, string districtCode)
    {
        var atIndex = baseEmail.IndexOf('@');
        if (atIndex <= 0)
        {
            return NormalizeEmail($"{baseEmail}.{districtCode}");
        }

        var localPart = baseEmail[..atIndex];
        var domain = baseEmail[(atIndex + 1)..];
        return NormalizeEmail($"{localPart}.{districtCode}@{domain}");
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    /// <summary>
    /// A fresh cryptographically random password for an account nobody signs in as.
    /// </summary>
    /// <remarks>
    /// 256 bits from <see cref="RandomNumberGenerator"/> — a CSPRNG, not <c>Random</c>, because the
    /// whole point is that the value is unguessable. Base64 keeps it printable (44 characters, so
    /// comfortably inside the 8–128 the registration endpoint allows and inside BCrypt's 72-byte
    /// input limit, which would otherwise silently truncate it).
    ///
    /// <para>
    /// The return value is used once, by <see cref="EnsureOwnerAsync"/>, as the argument to the
    /// password hasher. It is never logged, never written to the database, never returned to a
    /// caller and never stored in a field: nothing in this process can reconstruct it after the
    /// hash is taken, which is exactly the property wanted — an account with no known password
    /// cannot be signed into by anyone, including whoever reads this repository.
    /// </para>
    /// </remarks>
    private static string GenerateUnusableRandomPassword() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
