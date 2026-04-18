using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Infrastructure.DependencyInjection.DevelopmentSeed;

/// <summary>
/// Declarative, fully-static development seed data.
/// All identifiers are fixed GUIDs so repeated seed runs are idempotent.
/// Fields referencing related records (owner email, category slug, listing id) are resolved at runtime by the seeder.
/// </summary>
internal static class DevelopmentSeedData
{
    public sealed record SeedCategory(Guid Id, string Name, string Slug);

    public sealed record SeedUser(
        Guid Id,
        string Email,
        string FirstName,
        string LastName,
        UserRole Role,
        bool IsBlocked);

    public sealed record SeedListing(
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
        ListingStatus Status,
        int CreatedDaysAgo,
        int UpdatedDaysAgo);

    public sealed record SeedListingImage(
        Guid Id,
        Guid ListingId,
        string Url,
        bool IsPrimary,
        int SortOrder);

    public sealed record SeedFavorite(
        Guid Id,
        string UserEmail,
        Guid ListingId,
        int CreatedDaysAgo);

    public sealed record SeedBooking(
        Guid Id,
        Guid ListingId,
        string RenterEmail,
        int StartDaysFromToday,
        int DurationDays,
        BookingStatus Status,
        int ExpiresAtHoursFromNow,
        int CreatedDaysAgo);

    public static class ListingIds
    {
        public static readonly Guid DowntownStudioLoft     = new("22222222-0001-4000-9000-000000000001");
        public static readonly Guid LakesideVilla          = new("22222222-0002-4000-9000-000000000002");
        public static readonly Guid CompactCityCar         = new("22222222-0003-4000-9000-000000000003");
        public static readonly Guid ProfessionalCameraKit  = new("22222222-0004-4000-9000-000000000004");
        public static readonly Guid RoadBikeCarbon         = new("22222222-0005-4000-9000-000000000005");
        public static readonly Guid ModernSofaSet          = new("22222222-0006-4000-9000-000000000006");
        public static readonly Guid EventSoundSystem       = new("22222222-0007-4000-9000-000000000007");
        public static readonly Guid BoardGameCollection    = new("22222222-0008-4000-9000-000000000008");
        public static readonly Guid PowerDrillProKit       = new("22222222-0009-4000-9000-000000000009");
        public static readonly Guid DslrStarterKit         = new("22222222-000a-4000-9000-00000000000a");
        public static readonly Guid BrokenLaptopCollection = new("22222222-000b-4000-9000-00000000000b");
        public static readonly Guid OldBicycleTrailer      = new("22222222-000c-4000-9000-00000000000c");

        // Pre-existing listing kept for backward compatibility with earlier seed runs.
        // Referenced by the 'Completed' seed booking so QA can see a past booking on a well-known listing.
        public static readonly Guid CityCenterApartment    = new("a8b2bda2-5f5d-4f6a-a500-4e7ee224f9f9");
    }

    public static readonly SeedCategory[] Categories =
    [
        new(new Guid("f450befb-b2af-4f1e-8f34-0f9fd70d9c96"), "Apartments",      "apartments"),
        new(new Guid("d1f1e1d9-3a9d-4cde-9a7a-213844e0f4d8"), "Houses",          "houses"),
        new(new Guid("cb2f147c-95f2-4f69-b065-c01ee3464f3a"), "Cars",            "cars"),
        new(new Guid("8f4b38ff-2e27-4f23-957c-2ea1a2f3554a"), "Electronics",     "electronics"),
        new(new Guid("d41f8b63-e019-4f8e-8bc6-0ab90d6f8d0f"), "Toys",            "toys"),
        new(new Guid("f91a1f36-2063-4a7f-b4b1-65f30c6f6ef5"), "Tools",           "tools"),
        new(new Guid("7bbe3a8e-2d14-4a9e-8f19-5c4a7b0d3e21"), "Furniture",       "furniture"),
        new(new Guid("9c2fbe07-8a4d-4b1f-95c3-2e6a8d4f1c05"), "Cameras",         "cameras"),
        new(new Guid("3d8e2a14-7c5b-4e9f-9a12-6b8d4e2f1a03"), "Bikes",           "bikes"),
        new(new Guid("5f1c7b2e-4a8d-4e13-b2c5-7e1f8a9d3c04"), "Event Equipment", "event-equipment")
    ];

    public static readonly SeedUser[] Users =
    [
        new(
            new Guid("11111111-0001-4000-9000-000000000001"),
            DevelopmentSeedCredentials.AdminEmail,
            "Alex", "Admin",
            UserRole.Admin, IsBlocked: false),
        new(
            new Guid("11111111-0002-4000-9000-000000000002"),
            DevelopmentSeedCredentials.OwnerEmail,
            "Olivia", "Owner",
            UserRole.User, IsBlocked: false),
        new(
            new Guid("11111111-0003-4000-9000-000000000003"),
            DevelopmentSeedCredentials.RenterEmail,
            "Ryan", "Renter",
            UserRole.User, IsBlocked: false),
        new(
            new Guid("11111111-0004-4000-9000-000000000004"),
            DevelopmentSeedCredentials.SecondUserEmail,
            "Sam", "User",
            UserRole.User, IsBlocked: false),
        new(
            new Guid("11111111-0005-4000-9000-000000000005"),
            DevelopmentSeedCredentials.BlockedEmail,
            "Ben", "Blocked",
            UserRole.User, IsBlocked: true)
    ];

    public static readonly SeedListing[] Listings =
    [
        // ---- 7 Approved (owner@rental.local owns several) ----
        new(
            ListingIds.DowntownStudioLoft,
            "Downtown Studio Loft",
            "Bright studio loft in the heart of the city with premium finishes, fast Wi-Fi, and self check-in.",
            "apartments", DevelopmentSeedCredentials.OwnerEmail,
            58m, "USD", "Armenia", "Yerevan", "8 Saryan St",
            40.1856m, 44.5126m,
            ListingStatus.Approved, CreatedDaysAgo: 14, UpdatedDaysAgo: 2),
        new(
            ListingIds.LakesideVilla,
            "Lakeside Villa",
            "Three-bedroom villa with private terrace and panoramic lake view. Ideal for family weekends.",
            "houses", DevelopmentSeedCredentials.OwnerEmail,
            180m, "USD", "Armenia", "Sevan", "1 Shore Road",
            40.5539m, 44.9269m,
            ListingStatus.Approved, CreatedDaysAgo: 12, UpdatedDaysAgo: 3),
        new(
            ListingIds.CompactCityCar,
            "Compact City Car",
            "Fuel-efficient compact car, easy to park, ideal for city commuting and short errands.",
            "cars", DevelopmentSeedCredentials.OwnerEmail,
            38m, "USD", "Armenia", "Yerevan", "27 Baghramyan Ave",
            40.1910m, 44.5132m,
            ListingStatus.Approved, CreatedDaysAgo: 11, UpdatedDaysAgo: 4),
        new(
            ListingIds.ProfessionalCameraKit,
            "Professional Camera Kit",
            "Full-frame mirrorless camera with 24-70mm lens, extra batteries, and carry case.",
            "cameras", DevelopmentSeedCredentials.OwnerEmail,
            55m, "USD", "Armenia", "Yerevan", "19 Isahakyan St",
            40.1887m, 44.5134m,
            ListingStatus.Approved, CreatedDaysAgo: 10, UpdatedDaysAgo: 1),
        new(
            ListingIds.RoadBikeCarbon,
            "Carbon Frame Road Bike",
            "Lightweight carbon road bike in size M, perfect for weekend rides and light training.",
            "bikes", DevelopmentSeedCredentials.OwnerEmail,
            22m, "USD", "Armenia", "Yerevan", "45 Teryan St",
            40.1861m, 44.5159m,
            ListingStatus.Approved, CreatedDaysAgo: 9, UpdatedDaysAgo: 2),
        new(
            ListingIds.ModernSofaSet,
            "Modern Sofa Set",
            "Clean-design three-seat sofa plus matching armchair. Great for short-term rental staging.",
            "furniture", DevelopmentSeedCredentials.OwnerEmail,
            35m, "USD", "Armenia", "Yerevan", "10 Nalbandyan St",
            40.1795m, 44.5089m,
            ListingStatus.Approved, CreatedDaysAgo: 8, UpdatedDaysAgo: 1),
        new(
            ListingIds.EventSoundSystem,
            "Event Sound System",
            "Complete PA sound system with wireless microphones and stands. Delivery available.",
            "event-equipment", DevelopmentSeedCredentials.OwnerEmail,
            90m, "USD", "Armenia", "Yerevan", "5 Republic Square",
            40.1776m, 44.5126m,
            ListingStatus.Approved, CreatedDaysAgo: 7, UpdatedDaysAgo: 2),

        // ---- 3 PendingApproval (admin moderation queue) ----
        new(
            ListingIds.BoardGameCollection,
            "Board Game Collection",
            "Curated board-game collection covering strategy, party, and family titles.",
            "toys", DevelopmentSeedCredentials.OwnerEmail,
            10m, "USD", "Armenia", "Yerevan", "18 Pushkin St",
            40.1831m, 44.5100m,
            ListingStatus.PendingApproval, CreatedDaysAgo: 4, UpdatedDaysAgo: 1),
        new(
            ListingIds.PowerDrillProKit,
            "Power Drill Pro Kit",
            "Heavy-duty cordless drill with two batteries, charger, and mixed bit set.",
            "tools", DevelopmentSeedCredentials.OwnerEmail,
            18m, "USD", "Armenia", "Gyumri", "25 Abovyan St",
            40.7850m, 43.8453m,
            ListingStatus.PendingApproval, CreatedDaysAgo: 3, UpdatedDaysAgo: 1),
        new(
            ListingIds.DslrStarterKit,
            "DSLR Starter Kit",
            "Entry-level DSLR with 18-55mm kit lens, memory card, and tripod. Ideal for beginners.",
            "cameras", DevelopmentSeedCredentials.OwnerEmail,
            28m, "USD", "Armenia", "Yerevan", "12 Komitas Ave",
            40.2016m, 44.4915m,
            ListingStatus.PendingApproval, CreatedDaysAgo: 2, UpdatedDaysAgo: 0),

        // ---- 1 Rejected ----
        new(
            ListingIds.BrokenLaptopCollection,
            "Broken Laptop Collection",
            "Non-working laptops intended for parts. Submitted for moderation testing.",
            "electronics", DevelopmentSeedCredentials.OwnerEmail,
            5m, "USD", "Armenia", "Yerevan", "3 Mashtots Ave",
            40.1833m, 44.5150m,
            ListingStatus.Rejected, CreatedDaysAgo: 6, UpdatedDaysAgo: 5),

        // ---- 1 Archived ----
        new(
            ListingIds.OldBicycleTrailer,
            "Old Bicycle Trailer",
            "Used bicycle trailer retired from active listings. Archived for history testing.",
            "bikes", DevelopmentSeedCredentials.OwnerEmail,
            8m, "USD", "Armenia", "Yerevan", "14 Amiryan St",
            40.1811m, 44.5109m,
            ListingStatus.Archived, CreatedDaysAgo: 30, UpdatedDaysAgo: 15)
    ];

    // Image gallery: every new listing gets at least one primary image; several have a multi-image gallery.
    // URLs use picsum.photos seeds so dev demos render real images without local file setup.
    public static readonly SeedListingImage[] ListingImages =
    [
        new(new Guid("33333333-0001-4000-9000-000000000001"), ListingIds.DowntownStudioLoft,     "https://picsum.photos/seed/downtown-studio-1/1200/800", IsPrimary: true,  SortOrder: 0),
        new(new Guid("33333333-0001-4000-9000-000000000002"), ListingIds.DowntownStudioLoft,     "https://picsum.photos/seed/downtown-studio-2/1200/800", IsPrimary: false, SortOrder: 1),
        new(new Guid("33333333-0002-4000-9000-000000000001"), ListingIds.LakesideVilla,          "https://picsum.photos/seed/lakeside-villa-1/1200/800",  IsPrimary: true,  SortOrder: 0),
        new(new Guid("33333333-0002-4000-9000-000000000002"), ListingIds.LakesideVilla,          "https://picsum.photos/seed/lakeside-villa-2/1200/800",  IsPrimary: false, SortOrder: 1),
        new(new Guid("33333333-0002-4000-9000-000000000003"), ListingIds.LakesideVilla,          "https://picsum.photos/seed/lakeside-villa-3/1200/800",  IsPrimary: false, SortOrder: 2),
        new(new Guid("33333333-0003-4000-9000-000000000001"), ListingIds.CompactCityCar,         "https://picsum.photos/seed/compact-city-car/1200/800",  IsPrimary: true,  SortOrder: 0),
        new(new Guid("33333333-0004-4000-9000-000000000001"), ListingIds.ProfessionalCameraKit,  "https://picsum.photos/seed/pro-camera-kit-1/1200/800",  IsPrimary: true,  SortOrder: 0),
        new(new Guid("33333333-0004-4000-9000-000000000002"), ListingIds.ProfessionalCameraKit,  "https://picsum.photos/seed/pro-camera-kit-2/1200/800",  IsPrimary: false, SortOrder: 1),
        new(new Guid("33333333-0005-4000-9000-000000000001"), ListingIds.RoadBikeCarbon,         "https://picsum.photos/seed/road-bike/1200/800",         IsPrimary: true,  SortOrder: 0),
        new(new Guid("33333333-0006-4000-9000-000000000001"), ListingIds.ModernSofaSet,          "https://picsum.photos/seed/sofa-set-1/1200/800",        IsPrimary: true,  SortOrder: 0),
        new(new Guid("33333333-0006-4000-9000-000000000002"), ListingIds.ModernSofaSet,          "https://picsum.photos/seed/sofa-set-2/1200/800",        IsPrimary: false, SortOrder: 1),
        new(new Guid("33333333-0007-4000-9000-000000000001"), ListingIds.EventSoundSystem,       "https://picsum.photos/seed/event-sound/1200/800",       IsPrimary: true,  SortOrder: 0),
        new(new Guid("33333333-0008-4000-9000-000000000001"), ListingIds.BoardGameCollection,    "https://picsum.photos/seed/board-games/1200/800",       IsPrimary: true,  SortOrder: 0),
        new(new Guid("33333333-0009-4000-9000-000000000001"), ListingIds.PowerDrillProKit,       "https://picsum.photos/seed/power-drill/1200/800",       IsPrimary: true,  SortOrder: 0),
        new(new Guid("33333333-000a-4000-9000-00000000000a"), ListingIds.DslrStarterKit,         "https://picsum.photos/seed/dslr-starter/1200/800",      IsPrimary: true,  SortOrder: 0),
        new(new Guid("33333333-000b-4000-9000-00000000000b"), ListingIds.BrokenLaptopCollection, "https://picsum.photos/seed/broken-laptops/1200/800",    IsPrimary: true,  SortOrder: 0),
        new(new Guid("33333333-000c-4000-9000-00000000000c"), ListingIds.OldBicycleTrailer,      "https://picsum.photos/seed/old-bike-trailer/1200/800",  IsPrimary: true,  SortOrder: 0)
    ];

    public static readonly SeedFavorite[] Favorites =
    [
        new(new Guid("44444444-0001-4000-9000-000000000001"), DevelopmentSeedCredentials.RenterEmail,     ListingIds.LakesideVilla,         CreatedDaysAgo: 3),
        new(new Guid("44444444-0002-4000-9000-000000000002"), DevelopmentSeedCredentials.RenterEmail,     ListingIds.DowntownStudioLoft,    CreatedDaysAgo: 2),
        new(new Guid("44444444-0003-4000-9000-000000000003"), DevelopmentSeedCredentials.RenterEmail,     ListingIds.ProfessionalCameraKit, CreatedDaysAgo: 1),
        new(new Guid("44444444-0004-4000-9000-000000000004"), DevelopmentSeedCredentials.RenterEmail,     ListingIds.CityCenterApartment,   CreatedDaysAgo: 1),
        new(new Guid("44444444-0005-4000-9000-000000000005"), DevelopmentSeedCredentials.SecondUserEmail, ListingIds.ModernSofaSet,         CreatedDaysAgo: 2)
    ];

    // Five bookings covering every state the UI needs to render.
    // Dates are relative to today, TotalPrice is calculated by the seeder as (inclusive days) x PricePerDay.
    // Renter is never the listing owner; no Approved bookings overlap (they are on distinct listings).
    public static readonly SeedBooking[] Bookings =
    [
        new(
            new Guid("55555555-0001-4000-9000-000000000001"),
            ListingIds.LakesideVilla,
            DevelopmentSeedCredentials.RenterEmail,
            StartDaysFromToday: 14, DurationDays: 4,
            BookingStatus.Pending,
            ExpiresAtHoursFromNow: 24, CreatedDaysAgo: 0),
        new(
            new Guid("55555555-0002-4000-9000-000000000002"),
            ListingIds.DowntownStudioLoft,
            DevelopmentSeedCredentials.RenterEmail,
            StartDaysFromToday: 5, DurationDays: 4,
            BookingStatus.Approved,
            ExpiresAtHoursFromNow: -24, CreatedDaysAgo: 2),
        new(
            new Guid("55555555-0003-4000-9000-000000000003"),
            ListingIds.CompactCityCar,
            DevelopmentSeedCredentials.RenterEmail,
            StartDaysFromToday: 10, DurationDays: 3,
            BookingStatus.Rejected,
            ExpiresAtHoursFromNow: -2, CreatedDaysAgo: 3),
        new(
            new Guid("55555555-0004-4000-9000-000000000004"),
            ListingIds.ProfessionalCameraKit,
            DevelopmentSeedCredentials.RenterEmail,
            StartDaysFromToday: 20, DurationDays: 3,
            BookingStatus.Expired,
            ExpiresAtHoursFromNow: -1, CreatedDaysAgo: 3),
        new(
            new Guid("55555555-0005-4000-9000-000000000005"),
            ListingIds.CityCenterApartment,
            DevelopmentSeedCredentials.RenterEmail,
            StartDaysFromToday: -10, DurationDays: 4,
            BookingStatus.Completed,
            ExpiresAtHoursFromNow: -240, CreatedDaysAgo: 14)
    ];
}
