using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Infrastructure.DependencyInjection.DevelopmentSeed;

/// <summary>
/// Declarative, fully-static development seed data for the child-toys rental MVP.
/// All identifiers are fixed GUIDs so repeated seed runs are idempotent.
/// Fields referencing related records (owner email, category slug, listing id) are resolved at runtime by the seeder.
/// </summary>
internal static class DevelopmentSeedData
{
    public sealed record SeedCategory(Guid Id, string Name, string Slug, string? IconName, string? ImageUrl, int DisplayOrder);

    public sealed record SeedUser(
        Guid Id,
        string Email,
        string FirstName,
        string LastName,
        UserRole Role,
        bool IsBlocked,
        string? PhoneNumber = null);

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
        int UpdatedDaysAgo,
        int? AgeFromMonths,
        int? AgeToMonths,
        string? Condition,
        string? HygieneNotes,
        string? SafetyNotes,
        decimal? DepositAmount,
        string? RejectionReason = null);

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
        int CreatedDaysAgo,
        // Completion handshake (only meaningful for ReturnMarked bookings).
        BookingParty? ReturnInitiatedBy = null,
        int? ReturnMarkedHoursAgo = null,
        // Owner's reason when rejected (known reason code or free text).
        string? RejectionReason = null);

    // Reviews resolve reviewer/reviewee/listing from the referenced booking at seed time.
    public sealed record SeedToyReview(
        Guid Id, Guid BookingId,
        int Overall, int Condition, int Cleanliness, int Value, int Fun, int Description,
        string? Comment, int CreatedDaysAgo);

    public sealed record SeedOwnerReview(
        Guid Id, Guid BookingId,
        int Communication, int Pickup, int Friendliness,
        string? Comment, int CreatedDaysAgo);

    public sealed record SeedRenterReview(
        Guid Id, Guid BookingId,
        int Communication, int Returned, int Care, int WouldRent,
        string? Comment, int CreatedDaysAgo);

    // Toy-rental MVP listing ids. Prefix `77777777-` marks them as the toy-MVP seed cohort.
    public static class ListingIds
    {
        // ---- Additional approved listings owned by demo_owner@toyrent.am ----
        public static readonly Guid StemScienceDiscoveryKit  = new("77777777-000b-4000-9000-00000000000b");
        public static readonly Guid ClassicBoardGameTrio     = new("77777777-000c-4000-9000-00000000000c");
        public static readonly Guid PartyFunActivityPack     = new("77777777-000d-4000-9000-00000000000d");
        public static readonly Guid WoodenTrainSet           = new("77777777-000e-4000-9000-00000000000e");
        public static readonly Guid KidsArtEasel             = new("77777777-000f-4000-9000-00000000000f");
        public static readonly Guid LegoDuploStarterSet     = new("77777777-0001-4000-9000-000000000001");
        public static readonly Guid MontessoriWoodenToySet  = new("77777777-0002-4000-9000-000000000002");
        public static readonly Guid BabyActivityGym         = new("77777777-0003-4000-9000-000000000003");
        public static readonly Guid KidsBalanceBike         = new("77777777-0004-4000-9000-000000000004");
        public static readonly Guid OutdoorBackyardSlide    = new("77777777-0005-4000-9000-000000000005");
        public static readonly Guid ChildrensPuzzleBundle   = new("77777777-0006-4000-9000-000000000006");
        public static readonly Guid ToyKitchenSet           = new("77777777-0007-4000-9000-000000000007");
        public static readonly Guid BoardGameFamilyBundle   = new("77777777-0008-4000-9000-000000000008");
        public static readonly Guid BirthdayPartyToyPack    = new("77777777-0009-4000-9000-000000000009");
        public static readonly Guid SoftPlayFoamSet         = new("77777777-000a-4000-9000-00000000000a");
    }

    // Toy categories. Slugs are stable, lowercase, hyphenated and unique.
    // DisplayOrder controls carousel order on the home page.
    // IconName uses PrimeIcons class names (without the "pi " prefix).
    public static readonly SeedCategory[] Categories =
    [
        new(new Guid("c0000004-0000-4000-9000-000000000004"), "Baby Toys",        "baby-toys",        IconName: "pi-heart",    ImageUrl: "/assets/categories/baby-toys.svg",        DisplayOrder: 1),
        new(new Guid("c0000002-0000-4000-9000-000000000002"), "Building Blocks",  "building-blocks",  IconName: "pi-box",      ImageUrl: "/assets/categories/building-blocks.svg",  DisplayOrder: 2),
        new(new Guid("c0000001-0000-4000-9000-000000000001"), "Educational Toys", "educational-toys", IconName: "pi-book",     ImageUrl: "/assets/categories/educational-toys.svg", DisplayOrder: 3),
        new(new Guid("c0000003-0000-4000-9000-000000000003"), "Outdoor Toys",     "outdoor-toys",     IconName: "pi-sun",      ImageUrl: "/assets/categories/outdoor-toys.svg",     DisplayOrder: 4),
        new(new Guid("c0000007-0000-4000-9000-000000000007"), "Ride-On Toys",     "ride-on-toys",     IconName: "pi-car",      ImageUrl: "/assets/categories/ride-on-toys.svg",     DisplayOrder: 5),
        new(new Guid("c0000006-0000-4000-9000-000000000006"), "Pretend Play",     "pretend-play",     IconName: "pi-palette",  ImageUrl: "/assets/categories/pretend-play.svg",     DisplayOrder: 6),
        new(new Guid("c0000009-0000-4000-9000-000000000009"), "Montessori Toys",  "montessori-toys",  IconName: "pi-leaf",     ImageUrl: "/assets/categories/montessori-toys.svg",  DisplayOrder: 7),
        new(new Guid("c0000008-0000-4000-9000-000000000008"), "Puzzles",          "puzzles",          IconName: "pi-th-large", ImageUrl: "/assets/categories/puzzles.svg",           DisplayOrder: 8),
        new(new Guid("c0000005-0000-4000-9000-000000000005"), "Board Games",      "board-games",      IconName: "pi-table",    ImageUrl: "/assets/categories/board-games.svg",      DisplayOrder: 9),
        new(new Guid("c000000a-0000-4000-9000-00000000000a"), "Party Toys",       "party-toys",       IconName: "pi-gift",     ImageUrl: "/assets/categories/party-toys.svg",       DisplayOrder: 10)
    ];

    public static readonly SeedUser[] Users =
    [
        new(
            new Guid("11111111-0001-4000-9000-000000000001"),
            DevelopmentSeedCredentials.AdminEmail,
            "Alex", "Admin",
            UserRole.Admin, IsBlocked: false, PhoneNumber: "+374 55 100 001"),
        new(
            new Guid("11111111-0002-4000-9000-000000000002"),
            DevelopmentSeedCredentials.OwnerEmail,
            "Olivia", "Owner",
            UserRole.User, IsBlocked: false, PhoneNumber: "+374 99 100 002"),
        new(
            new Guid("11111111-0003-4000-9000-000000000003"),
            DevelopmentSeedCredentials.RenterEmail,
            "Ryan", "Renter",
            UserRole.User, IsBlocked: false, PhoneNumber: "+374 91 100 003"),
        new(
            new Guid("11111111-0004-4000-9000-000000000004"),
            DevelopmentSeedCredentials.SecondUserEmail,
            "Sam", "User",
            UserRole.User, IsBlocked: false, PhoneNumber: "+374 93 100 004"),
        new(
            new Guid("11111111-0005-4000-9000-000000000005"),
            DevelopmentSeedCredentials.BlockedEmail,
            "Ben", "Blocked",
            UserRole.User, IsBlocked: true, PhoneNumber: "+374 77 100 005"),

        // ---- Docker / public demo accounts (toyrent.am) ----
        new(
            new Guid("11111111-0006-4000-9000-000000000006"),
            DevelopmentSeedCredentials.DemoAdminEmail,
            "Admin", "ToyRent",
            UserRole.Admin, IsBlocked: false, PhoneNumber: "+374 55 200 001"),
        new(
            new Guid("11111111-0007-4000-9000-000000000007"),
            DevelopmentSeedCredentials.DemoOwnerEmail,
            "Demo", "Owner",
            UserRole.User, IsBlocked: false, PhoneNumber: "+374 99 200 002"),
        new(
            new Guid("11111111-0008-4000-9000-000000000008"),
            DevelopmentSeedCredentials.DemoRenterEmail,
            "Demo", "Renter",
            UserRole.User, IsBlocked: false, PhoneNumber: "+374 91 200 003")
    ];

    // 10 toy listings (7 Approved, 2 PendingApproval, 1 Rejected), all owned by demo owner, Yerevan-focused.
    public static readonly SeedListing[] Listings =
    [
        new(
            ListingIds.LegoDuploStarterSet,
            "LEGO Duplo Starter Set",
            "Classic LEGO Duplo starter set with 80+ chunky pieces. Sanitized between rentals and stored in a sealed box.",
            "building-blocks", DevelopmentSeedCredentials.OwnerEmail,
            6m, "USD", "Armenia", "Yerevan", "8 Saryan St",
            40.1856m, 44.5126m,
            ListingStatus.Approved, CreatedDaysAgo: 14, UpdatedDaysAgo: 2,
            AgeFromMonths: 18, AgeToMonths: 60,
            Condition: "Excellent",
            HygieneNotes: "Wiped down with child-safe disinfectant after every return. Complete piece count verified.",
            SafetyNotes: "All pieces are large enough to comply with EN 71-1 small-parts requirements. No loose batteries.",
            DepositAmount: 30m),
        new(
            ListingIds.MontessoriWoodenToySet,
            "Montessori Wooden Toy Set",
            "Six-piece natural wood Montessori set: shape sorter, stacking rings, threading beads, peg board and counting bars.",
            "montessori-toys", DevelopmentSeedCredentials.OwnerEmail,
            8m, "USD", "Armenia", "Yerevan", "27 Baghramyan Ave",
            40.1910m, 44.5132m,
            ListingStatus.Approved, CreatedDaysAgo: 12, UpdatedDaysAgo: 3,
            AgeFromMonths: 24, AgeToMonths: 72,
            Condition: "Like new",
            HygieneNotes: "Wood pieces wiped with a damp cloth and left to fully air-dry between rentals.",
            SafetyNotes: "Smooth, splinter-free finish. Non-toxic water-based stain. No magnets, no detachable small parts.",
            DepositAmount: 35m),
        new(
            ListingIds.BabyActivityGym,
            "Baby Activity Gym",
            "Padded baby activity gym with detachable hanging toys, a mirror, a textured teether and a soft rattle.",
            "baby-toys", DevelopmentSeedCredentials.OwnerEmail,
            5m, "USD", "Armenia", "Yerevan", "19 Isahakyan St",
            40.1887m, 44.5134m,
            ListingStatus.Approved, CreatedDaysAgo: 10, UpdatedDaysAgo: 1,
            AgeFromMonths: 0, AgeToMonths: 12,
            Condition: "Excellent",
            HygieneNotes: "Removable mat is machine-washed at 60 °C between rentals. Hanging toys are surface-sanitized.",
            SafetyNotes: "All attachments are double-stitched and torque-tested. Suitable for supervised tummy time.",
            DepositAmount: 25m),
        new(
            ListingIds.KidsBalanceBike,
            "Kids Balance Bike",
            "Lightweight 12-inch balance bike with adjustable seat (30-42 cm) and puncture-resistant tyres.",
            "ride-on-toys", DevelopmentSeedCredentials.OwnerEmail,
            7m, "USD", "Armenia", "Yerevan", "45 Teryan St",
            40.1861m, 44.5159m,
            ListingStatus.Approved, CreatedDaysAgo: 9, UpdatedDaysAgo: 2,
            AgeFromMonths: 24, AgeToMonths: 60,
            Condition: "Good",
            HygieneNotes: "Frame and grips wiped with disinfectant; saddle cover wiped with antibacterial spray.",
            SafetyNotes: "Helmet not included. Owner recommends a fitted helmet and supervised use on flat surfaces.",
            DepositAmount: 40m),
        new(
            ListingIds.OutdoorBackyardSlide,
            "Outdoor Backyard Slide",
            "Stable plastic backyard slide, ~1.2 m climb. Easy to wipe down. Great for small gardens and play days.",
            "outdoor-toys", DevelopmentSeedCredentials.OwnerEmail,
            10m, "USD", "Armenia", "Yerevan", "10 Nalbandyan St",
            40.1795m, 44.5089m,
            ListingStatus.Approved, CreatedDaysAgo: 8, UpdatedDaysAgo: 1,
            AgeFromMonths: 18, AgeToMonths: 72,
            Condition: "Good",
            HygieneNotes: "Surfaces wiped with mild soap and water after every rental, then dried.",
            SafetyNotes: "Must be placed on level ground. Owner provides anti-slip pads. Max user weight 25 kg.",
            DepositAmount: 50m),
        new(
            ListingIds.ChildrensPuzzleBundle,
            "Children's Puzzle Bundle",
            "Bundle of four wooden puzzles (12, 24, 48 and 60 pieces). All pieces verified present before pickup.",
            "puzzles", DevelopmentSeedCredentials.OwnerEmail,
            4m, "USD", "Armenia", "Yerevan", "18 Pushkin St",
            40.1831m, 44.5100m,
            ListingStatus.Approved, CreatedDaysAgo: 7, UpdatedDaysAgo: 2,
            AgeFromMonths: 36, AgeToMonths: 96,
            Condition: "Like new",
            HygieneNotes: "Pieces wiped with a slightly damp cloth and air-dried between rentals.",
            SafetyNotes: "Smallest pieces are above the 3-year-old small-parts threshold. Not recommended under 36 months.",
            DepositAmount: 20m),
        new(
            ListingIds.ToyKitchenSet,
            "Wooden Toy Kitchen Set",
            "Wooden play kitchen with stove, sink, oven door and 20 accessories (utensils, pots, play food).",
            "pretend-play", DevelopmentSeedCredentials.OwnerEmail,
            9m, "USD", "Armenia", "Yerevan", "5 Republic Square",
            40.1776m, 44.5126m,
            ListingStatus.Approved, CreatedDaysAgo: 6, UpdatedDaysAgo: 1,
            AgeFromMonths: 30, AgeToMonths: 96,
            Condition: "Excellent",
            HygieneNotes: "Wood surfaces wiped with food-safe cleaner; small accessories washed in soapy water.",
            SafetyNotes: "Rounded edges. No glass, no magnets, no detachable small parts under 36 months.",
            DepositAmount: 40m),

        // ---- PendingApproval (admin moderation queue) ----
        new(
            ListingIds.BoardGameFamilyBundle,
            "Board Game Family Bundle",
            "Family game-night bundle: three age-appropriate board games covering memory, strategy and cooperation.",
            "board-games", DevelopmentSeedCredentials.OwnerEmail,
            5m, "USD", "Armenia", "Yerevan", "12 Komitas Ave",
            40.2016m, 44.4915m,
            ListingStatus.PendingApproval, CreatedDaysAgo: 3, UpdatedDaysAgo: 1,
            AgeFromMonths: 48, AgeToMonths: 144,
            Condition: "Like new",
            HygieneNotes: "Cards and pieces wiped down between rentals; boxes inspected for completeness.",
            SafetyNotes: "Contains small parts; not suitable under 36 months without supervision.",
            DepositAmount: 25m),
        new(
            ListingIds.BirthdayPartyToyPack,
            "Birthday Party Toy Pack",
            "Party toy pack: bean bags, foam darts, ring toss, pin-the-tail and a soft ball pool (50 balls).",
            "party-toys", DevelopmentSeedCredentials.OwnerEmail,
            12m, "USD", "Armenia", "Gyumri", "25 Abovyan St",
            40.7850m, 43.8453m,
            ListingStatus.PendingApproval, CreatedDaysAgo: 2, UpdatedDaysAgo: 0,
            AgeFromMonths: 36, AgeToMonths: 144,
            Condition: "Good",
            HygieneNotes: "Balls and fabric items washed; foam darts wiped with antibacterial wipes.",
            SafetyNotes: "Foam darts only. No projectile toys. Adult supervision recommended.",
            DepositAmount: 30m),

        // ---- Rejected ----
        new(
            ListingIds.SoftPlayFoamSet,
            "Soft Play Foam Set",
            "Foam soft-play set submitted for moderation testing. Will be rejected because hygiene notes were left empty.",
            "educational-toys", DevelopmentSeedCredentials.OwnerEmail,
            8m, "USD", "Armenia", "Yerevan", "3 Mashtots Ave",
            40.1833m, 44.5150m,
            ListingStatus.Rejected, CreatedDaysAgo: 6, UpdatedDaysAgo: 5,
            AgeFromMonths: 12, AgeToMonths: 60,
            Condition: "Used",
            HygieneNotes: null,
            SafetyNotes: null,
            DepositAmount: 25m,
            RejectionReason: "Hygiene notes are required. Please describe how the item is cleaned between rentals."),

        // ---- Additional approved listings owned by demo_owner@toyrent.am (listings 8–12 approved) ----
        new(
            ListingIds.StemScienceDiscoveryKit,
            "STEM Science Discovery Kit",
            "Hands-on science kit with 20+ experiments: volcano, crystal growing, slime, and simple circuit activities. All chemicals are child-safe and pre-measured.",
            "educational-toys", DevelopmentSeedCredentials.DemoOwnerEmail,
            7m, "USD", "Armenia", "Yerevan", "14 Tigranyan St",
            40.1862m, 44.5171m,
            ListingStatus.Approved, CreatedDaysAgo: 11, UpdatedDaysAgo: 2,
            AgeFromMonths: 60, AgeToMonths: 144,
            Condition: "Excellent",
            HygieneNotes: "Single-use chemical sachets replaced after each rental. Trays and tools washed with soapy water.",
            SafetyNotes: "Adult supervision required for all experiments. No open flames. Includes safety goggles.",
            DepositAmount: 20m),
        new(
            ListingIds.ClassicBoardGameTrio,
            "Classic Board Game Trio",
            "Three timeless board games in one bundle: Snakes & Ladders, Ludo, and a 100-piece junior jigsaw. All pieces verified complete.",
            "board-games", DevelopmentSeedCredentials.DemoOwnerEmail,
            5m, "USD", "Armenia", "Yerevan", "6 Hanrapetutyan St",
            40.1783m, 44.5139m,
            ListingStatus.Approved, CreatedDaysAgo: 9, UpdatedDaysAgo: 1,
            AgeFromMonths: 48, AgeToMonths: 144,
            Condition: "Like new",
            HygieneNotes: "Cards and tokens wiped with a dry cloth before return; boxes sealed with elastic for storage.",
            SafetyNotes: "Contains small pieces; not suitable for children under 3 years without supervision.",
            DepositAmount: 15m),
        new(
            ListingIds.PartyFunActivityPack,
            "Party Fun Activity Pack",
            "Complete party activity kit: parachute play cloth, bean bags, hula hoops (×2), jump rope, and a set of colourful cones. Perfect for birthdays and group play.",
            "party-toys", DevelopmentSeedCredentials.DemoOwnerEmail,
            11m, "USD", "Armenia", "Yerevan", "22 Azatutyan Ave",
            40.2102m, 44.4997m,
            ListingStatus.Approved, CreatedDaysAgo: 7, UpdatedDaysAgo: 1,
            AgeFromMonths: 36, AgeToMonths: 144,
            Condition: "Good",
            HygieneNotes: "Fabric items machine-washed after every rental. Hard plastic items wiped with antibacterial spray.",
            SafetyNotes: "Parachute activity requires adult supervision. Clear a flat open area of at least 4 × 4 m.",
            DepositAmount: 30m),
        new(
            ListingIds.WoodenTrainSet,
            "Wooden Train Set & Track (56 pcs)",
            "56-piece wooden train set with figure-of-eight track, bridges, tunnels, a station, two engines and six carriages. Compatible with major wooden-rail brands.",
            "building-blocks", DevelopmentSeedCredentials.DemoOwnerEmail,
            8m, "USD", "Armenia", "Yerevan", "31 Movses Khorenatsi St",
            40.1756m, 44.5068m,
            ListingStatus.Approved, CreatedDaysAgo: 5, UpdatedDaysAgo: 1,
            AgeFromMonths: 24, AgeToMonths: 84,
            Condition: "Excellent",
            HygieneNotes: "Track pieces and rolling stock wiped with a damp cloth and dried before packing. Piece count verified.",
            SafetyNotes: "No small detachable parts below 3-year-old threshold. Supervised use recommended under 24 months.",
            DepositAmount: 35m),
        new(
            ListingIds.KidsArtEasel,
            "Kids Double-Sided Art Easel",
            "Height-adjustable double-sided easel: whiteboard on one side, blackboard on the other, with a paper roll holder. Includes chalk, eraser, and 3 dry-erase markers.",
            "pretend-play", DevelopmentSeedCredentials.DemoOwnerEmail,
            6m, "USD", "Armenia", "Yerevan", "9 Arshakunyats Ave",
            40.1728m, 44.5205m,
            ListingStatus.Approved, CreatedDaysAgo: 4, UpdatedDaysAgo: 0,
            AgeFromMonths: 24, AgeToMonths: 96,
            Condition: "Like new",
            HygieneNotes: "Whiteboard and blackboard surfaces wiped clean before handover. Markers capped and tested.",
            SafetyNotes: "Child-safe, non-toxic chalk and markers. Easel folds flat for transport; locking pins provided.",
            DepositAmount: 20m)
    ];

    // Every listing gets at least one primary image; several have a multi-image gallery.
    // URLs use picsum.photos seeds so dev demos render without local file setup.
    public static readonly SeedListingImage[] ListingImages =
    [
        new(new Guid("33333333-0001-4000-9000-000000000001"), ListingIds.LegoDuploStarterSet,    "https://picsum.photos/seed/toy-lego-duplo-1/1200/800",   IsPrimary: true,  SortOrder: 0),
        new(new Guid("33333333-0001-4000-9000-000000000002"), ListingIds.LegoDuploStarterSet,    "https://picsum.photos/seed/toy-lego-duplo-2/1200/800",   IsPrimary: false, SortOrder: 1),
        new(new Guid("33333333-0002-4000-9000-000000000001"), ListingIds.MontessoriWoodenToySet, "https://picsum.photos/seed/toy-montessori-1/1200/800",   IsPrimary: true,  SortOrder: 0),
        new(new Guid("33333333-0002-4000-9000-000000000002"), ListingIds.MontessoriWoodenToySet, "https://picsum.photos/seed/toy-montessori-2/1200/800",   IsPrimary: false, SortOrder: 1),
        new(new Guid("33333333-0003-4000-9000-000000000001"), ListingIds.BabyActivityGym,        "https://picsum.photos/seed/toy-baby-gym-1/1200/800",     IsPrimary: true,  SortOrder: 0),
        new(new Guid("33333333-0004-4000-9000-000000000001"), ListingIds.KidsBalanceBike,        "https://picsum.photos/seed/toy-balance-bike-1/1200/800", IsPrimary: true,  SortOrder: 0),
        new(new Guid("33333333-0004-4000-9000-000000000002"), ListingIds.KidsBalanceBike,        "https://picsum.photos/seed/toy-balance-bike-2/1200/800", IsPrimary: false, SortOrder: 1),
        new(new Guid("33333333-0005-4000-9000-000000000001"), ListingIds.OutdoorBackyardSlide,   "https://picsum.photos/seed/toy-backyard-slide/1200/800", IsPrimary: true,  SortOrder: 0),
        new(new Guid("33333333-0006-4000-9000-000000000001"), ListingIds.ChildrensPuzzleBundle,  "https://picsum.photos/seed/toy-puzzle-bundle/1200/800",  IsPrimary: true,  SortOrder: 0),
        new(new Guid("33333333-0007-4000-9000-000000000001"), ListingIds.ToyKitchenSet,          "https://picsum.photos/seed/toy-kitchen-1/1200/800",      IsPrimary: true,  SortOrder: 0),
        new(new Guid("33333333-0007-4000-9000-000000000002"), ListingIds.ToyKitchenSet,          "https://picsum.photos/seed/toy-kitchen-2/1200/800",      IsPrimary: false, SortOrder: 1),
        new(new Guid("33333333-0008-4000-9000-000000000001"), ListingIds.BoardGameFamilyBundle,  "https://picsum.photos/seed/toy-board-games/1200/800",    IsPrimary: true,  SortOrder: 0),
        new(new Guid("33333333-0009-4000-9000-000000000001"), ListingIds.BirthdayPartyToyPack,   "https://picsum.photos/seed/toy-party-pack/1200/800",     IsPrimary: true,  SortOrder: 0),
        new(new Guid("33333333-000a-4000-9000-00000000000a"), ListingIds.SoftPlayFoamSet,           "https://picsum.photos/seed/toy-soft-play/1200/800",       IsPrimary: true,  SortOrder: 0),
        // Images for the 5 additional approved demo listings
        new(new Guid("33333333-000b-4000-9000-00000000000b"), ListingIds.StemScienceDiscoveryKit,   "https://picsum.photos/seed/toy-stem-science-1/1200/800",  IsPrimary: true,  SortOrder: 0),
        new(new Guid("33333333-000b-4000-9000-00000000000c"), ListingIds.StemScienceDiscoveryKit,   "https://picsum.photos/seed/toy-stem-science-2/1200/800",  IsPrimary: false, SortOrder: 1),
        new(new Guid("33333333-000c-4000-9000-00000000000b"), ListingIds.ClassicBoardGameTrio,      "https://picsum.photos/seed/toy-board-trio-1/1200/800",    IsPrimary: true,  SortOrder: 0),
        new(new Guid("33333333-000d-4000-9000-00000000000b"), ListingIds.PartyFunActivityPack,      "https://picsum.photos/seed/toy-party-fun-1/1200/800",     IsPrimary: true,  SortOrder: 0),
        new(new Guid("33333333-000d-4000-9000-00000000000c"), ListingIds.PartyFunActivityPack,      "https://picsum.photos/seed/toy-party-fun-2/1200/800",     IsPrimary: false, SortOrder: 1),
        new(new Guid("33333333-000e-4000-9000-00000000000b"), ListingIds.WoodenTrainSet,            "https://picsum.photos/seed/toy-train-set-1/1200/800",     IsPrimary: true,  SortOrder: 0),
        new(new Guid("33333333-000e-4000-9000-00000000000c"), ListingIds.WoodenTrainSet,            "https://picsum.photos/seed/toy-train-set-2/1200/800",     IsPrimary: false, SortOrder: 1),
        new(new Guid("33333333-000f-4000-9000-00000000000b"), ListingIds.KidsArtEasel,              "https://picsum.photos/seed/toy-art-easel-1/1200/800",     IsPrimary: true,  SortOrder: 0)
    ];

    public static readonly SeedFavorite[] Favorites =
    [
        new(new Guid("44444444-0001-4000-9000-000000000001"), DevelopmentSeedCredentials.RenterEmail,     ListingIds.MontessoriWoodenToySet, CreatedDaysAgo: 3),
        new(new Guid("44444444-0002-4000-9000-000000000002"), DevelopmentSeedCredentials.RenterEmail,     ListingIds.LegoDuploStarterSet,    CreatedDaysAgo: 2),
        new(new Guid("44444444-0003-4000-9000-000000000003"), DevelopmentSeedCredentials.RenterEmail,     ListingIds.BabyActivityGym,        CreatedDaysAgo: 1),
        new(new Guid("44444444-0004-4000-9000-000000000004"), DevelopmentSeedCredentials.SecondUserEmail, ListingIds.ToyKitchenSet,          CreatedDaysAgo: 2)
    ];

    // Bookings covering every state the UI needs to render.
    // Dates are relative to today, TotalPrice is calculated by the seeder as (inclusive days) x PricePerDay.
    // Renter is never the listing owner; no Approved bookings overlap (they are on distinct listings).
    public static readonly SeedBooking[] Bookings =
    [
        new(
            new Guid("55555555-0001-4000-9000-000000000001"),
            ListingIds.MontessoriWoodenToySet,
            DevelopmentSeedCredentials.RenterEmail,
            StartDaysFromToday: 14, DurationDays: 4,
            BookingStatus.Pending,
            ExpiresAtHoursFromNow: 24, CreatedDaysAgo: 0),
        new(
            new Guid("55555555-0002-4000-9000-000000000002"),
            ListingIds.LegoDuploStarterSet,
            DevelopmentSeedCredentials.RenterEmail,
            StartDaysFromToday: 5, DurationDays: 4,
            BookingStatus.Approved,
            ExpiresAtHoursFromNow: -24, CreatedDaysAgo: 2),
        new(
            new Guid("55555555-0003-4000-9000-000000000003"),
            ListingIds.KidsBalanceBike,
            DevelopmentSeedCredentials.RenterEmail,
            StartDaysFromToday: 10, DurationDays: 3,
            BookingStatus.Rejected,
            ExpiresAtHoursFromNow: -2, CreatedDaysAgo: 3,
            RejectionReason: "dates_unavailable"),
        new(
            new Guid("55555555-0004-4000-9000-000000000004"),
            ListingIds.OutdoorBackyardSlide,
            DevelopmentSeedCredentials.RenterEmail,
            StartDaysFromToday: 20, DurationDays: 2,
            BookingStatus.Expired,
            ExpiresAtHoursFromNow: -1, CreatedDaysAgo: 3),
        new(
            new Guid("55555555-0005-4000-9000-000000000005"),
            ListingIds.ToyKitchenSet,
            DevelopmentSeedCredentials.RenterEmail,
            StartDaysFromToday: -10, DurationDays: 4,
            BookingStatus.Completed,
            ExpiresAtHoursFromNow: -240, CreatedDaysAgo: 14),
        // Renter-cancelled booking — exercises the new cancellation flow in the UI.
        new(
            new Guid("55555555-0006-4000-9000-000000000006"),
            ListingIds.ChildrensPuzzleBundle,
            DevelopmentSeedCredentials.RenterEmail,
            StartDaysFromToday: 25, DurationDays: 3,
            BookingStatus.Cancelled,
            ExpiresAtHoursFromNow: -48, CreatedDaysAgo: 4),

        // ---- Completed bookings that back the seeded reviews (≥2 per listing/owner so
        //      aggregates clear the minimum-reviews threshold and render in the demo). ----
        new(new Guid("55555555-0007-4000-9000-000000000007"), ListingIds.ToyKitchenSet,
            DevelopmentSeedCredentials.SecondUserEmail,
            StartDaysFromToday: -20, DurationDays: 3, BookingStatus.Completed,
            ExpiresAtHoursFromNow: -360, CreatedDaysAgo: 22),
        new(new Guid("55555555-0008-4000-9000-000000000008"), ListingIds.StemScienceDiscoveryKit,
            DevelopmentSeedCredentials.RenterEmail,
            StartDaysFromToday: -30, DurationDays: 4, BookingStatus.Completed,
            ExpiresAtHoursFromNow: -600, CreatedDaysAgo: 33),
        new(new Guid("55555555-0009-4000-9000-000000000009"), ListingIds.StemScienceDiscoveryKit,
            DevelopmentSeedCredentials.SecondUserEmail,
            StartDaysFromToday: -25, DurationDays: 5, BookingStatus.Completed,
            ExpiresAtHoursFromNow: -480, CreatedDaysAgo: 28),
        new(new Guid("55555555-000a-4000-9000-00000000000a"), ListingIds.LegoDuploStarterSet,
            DevelopmentSeedCredentials.SecondUserEmail,
            StartDaysFromToday: -18, DurationDays: 3, BookingStatus.Completed,
            ExpiresAtHoursFromNow: -300, CreatedDaysAgo: 20),

        // ---- Completion-handshake demo bookings (owner@ listings, rented by renter@) ----
        // Active: rental in progress (started, not yet ended) — both sides can mark returned.
        new(new Guid("55555555-000b-4000-9000-00000000000b"), ListingIds.BabyActivityGym,
            DevelopmentSeedCredentials.RenterEmail,
            StartDaysFromToday: -2, DurationDays: 7, BookingStatus.Approved,
            ExpiresAtHoursFromNow: -48, CreatedDaysAgo: 5),
        // ReturnMarked by the renter — awaiting owner confirmation, never auto-completes.
        new(new Guid("55555555-000c-4000-9000-00000000000c"), ListingIds.KidsBalanceBike,
            DevelopmentSeedCredentials.RenterEmail,
            StartDaysFromToday: -5, DurationDays: 4, BookingStatus.ReturnMarked,
            ExpiresAtHoursFromNow: -120, CreatedDaysAgo: 7,
            ReturnInitiatedBy: BookingParty.Renter, ReturnMarkedHoursAgo: 6),
        // ReturnMarked by the owner — awaiting renter confirmation, auto-completes 48h after the mark.
        new(new Guid("55555555-000d-4000-9000-00000000000d"), ListingIds.ChildrensPuzzleBundle,
            DevelopmentSeedCredentials.RenterEmail,
            StartDaysFromToday: -6, DurationDays: 4, BookingStatus.ReturnMarked,
            ExpiresAtHoursFromNow: -144, CreatedDaysAgo: 8,
            ReturnInitiatedBy: BookingParty.Owner, ReturnMarkedHoursAgo: 6)
    ];

    // Toy reviews (renter → toy). Booking 0005 + 0007 give ToyKitchenSet two reviews.
    public static readonly SeedToyReview[] ToyReviews =
    [
        new(new Guid("66666666-0001-4000-9000-000000000001"), new Guid("55555555-0005-4000-9000-000000000005"),
            Overall: 5, Condition: 5, Cleanliness: 5, Value: 4, Fun: 5, Description: 5,
            "All pieces present and spotless. My son played non-stop — the kitchen is a winner.", CreatedDaysAgo: 5),
        new(new Guid("66666666-0002-4000-9000-000000000002"), new Guid("55555555-0007-4000-9000-000000000007"),
            Overall: 4, Condition: 4, Cleanliness: 5, Value: 4, Fun: 5, Description: 4,
            "Exactly as described and well sanitized. Sturdy pieces, nothing missing.", CreatedDaysAgo: 16),
        new(new Guid("66666666-0003-4000-9000-000000000003"), new Guid("55555555-0008-4000-9000-000000000008"),
            Overall: 5, Condition: 5, Cleanliness: 5, Value: 5, Fun: 5, Description: 5,
            "Fantastic STEM kit — the experiments kept the kids busy all weekend.", CreatedDaysAgo: 26),
        new(new Guid("66666666-0004-4000-9000-000000000004"), new Guid("55555555-0009-4000-9000-000000000009"),
            Overall: 4, Condition: 4, Cleanliness: 4, Value: 5, Fun: 4, Description: 4,
            "Great value. One sachet was running low but the owner sorted it instantly.", CreatedDaysAgo: 21)
    ];

    // Owner reviews (renter → owner). Owner@ gets two; DemoOwner@ gets two.
    public static readonly SeedOwnerReview[] OwnerReviews =
    [
        new(new Guid("77777771-0001-4000-9000-000000000001"), new Guid("55555555-0005-4000-9000-000000000005"),
            Communication: 5, Pickup: 5, Friendliness: 5,
            "Olivia was super communicative and flexible with pickup. Would happily rent again!", CreatedDaysAgo: 5),
        new(new Guid("77777771-0002-4000-9000-000000000002"), new Guid("55555555-0007-4000-9000-000000000007"),
            Communication: 5, Pickup: 4, Friendliness: 5,
            "Smooth handover, very friendly and on time.", CreatedDaysAgo: 16),
        new(new Guid("77777771-0003-4000-9000-000000000003"), new Guid("55555555-0008-4000-9000-000000000008"),
            Communication: 5, Pickup: 5, Friendliness: 5,
            "Quick replies and easy meetup. Highly recommend.", CreatedDaysAgo: 26),
        new(new Guid("77777771-0004-4000-9000-000000000004"), new Guid("55555555-0009-4000-9000-000000000009"),
            Communication: 4, Pickup: 5, Friendliness: 5,
            null, CreatedDaysAgo: 21)
    ];

    // Renter reviews (owner → renter).
    public static readonly SeedRenterReview[] RenterReviews =
    [
        new(new Guid("77777772-0001-4000-9000-000000000001"), new Guid("55555555-0005-4000-9000-000000000005"),
            Communication: 5, Returned: 5, Care: 4, WouldRent: 5,
            "Easy to coordinate, returned the set in great shape. Welcome anytime!", CreatedDaysAgo: 4),
        new(new Guid("77777772-0002-4000-9000-000000000002"), new Guid("55555555-0008-4000-9000-000000000008"),
            Communication: 5, Returned: 5, Care: 5, WouldRent: 5,
            null, CreatedDaysAgo: 25)
    ];
}
