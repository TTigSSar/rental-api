using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Infrastructure.DependencyInjection.DevelopmentSeed;

/// <summary>
/// Declarative, fully-static development seed data for the child-toys rental MVP.
/// All identifiers are fixed GUIDs so repeated seed runs are idempotent.
/// Fields referencing related records (owner email, category slug, listing id) are resolved at runtime by the seeder.
/// </summary>
internal static class DevelopmentSeedData
{
    public sealed record SeedCategory(
        Guid Id, string Name, string Slug, string? IconName, string? ImageUrl, int DisplayOrder, string? ColorHex = null);

    // Admin console Phase 6 ("Needs category fix"): a word/phrase that, found in a pending
    // listing's title or description, signals the category it belongs in. CategorySlug resolves
    // to the category id at seed time (same convention as SeedListing.CategorySlug).
    public sealed record SeedCategoryKeyword(string CategorySlug, string Keyword);

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
        int SortOrder,
        string FallbackUrl = "");

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

        // ---- 50-listing toy-catalogue expansion (2026-08), 5 per category across the 5 new
        //      owners + demo_owner@toyrent.am, spread across all 12 Yerevan districts. ----
        public static readonly Guid FisherPrice4In1OceanWondersBouncer = new("77777777-0010-4000-9000-000000000010");
        public static readonly Guid LEGOClassicCreativeBricksBox500Pcs = new("77777777-0011-4000-9000-000000000011");
        public static readonly Guid LeapFrogLeapStartInteractiveLearningSystem = new("77777777-0012-4000-9000-000000000012");
        public static readonly Guid SmobyOutdoorPlayhouse = new("77777777-0013-4000-9000-000000000013");
        public static readonly Guid LittleTikesCozyCoupe = new("77777777-0014-4000-9000-000000000014");
        public static readonly Guid MelissaDougWoodenDollhouse = new("77777777-0015-4000-9000-000000000015");
        public static readonly Guid HapePoundTapBench = new("77777777-0016-4000-9000-000000000016");
        public static readonly Guid Djeco100PieceFloorPuzzleFamilyReunion = new("77777777-0017-4000-9000-000000000017");
        public static readonly Guid HasbroGuessWhoClassic = new("77777777-0018-4000-9000-000000000018");
        public static readonly Guid LittleTikesInflatableBounceHouse = new("77777777-0019-4000-9000-000000000019");
        public static readonly Guid ChiccoBabySensesActivityGym = new("77777777-001a-4000-9000-00000000001a");
        public static readonly Guid MegaBloksFirstBuildersBigBuildingBag80Pcs = new("77777777-001b-4000-9000-00000000001b");
        public static readonly Guid LearningResourcesCodingCrittersRangerZip = new("77777777-001c-4000-9000-00000000001c");
        public static readonly Guid IntexInflatableKiddiePoolOceanPlayCenter = new("77777777-001d-4000-9000-00000000001d");
        public static readonly Guid RadioFlyerClassicRedTricycle = new("77777777-001e-4000-9000-00000000001e");
        public static readonly Guid Step2FixerUpperToolBench = new("77777777-001f-4000-9000-00000000001f");
        public static readonly Guid MelissaDougShapeSortingCube = new("77777777-0020-4000-9000-000000000020");
        public static readonly Guid Ravensburger200PieceDisneyPuzzle = new("77777777-0021-4000-9000-000000000021");
        public static readonly Guid HabaMyVeryFirstGamesOrchardCompare = new("77777777-0022-4000-9000-000000000022");
        public static readonly Guid IntexBallPitWith100Balls = new("77777777-0023-4000-9000-000000000023");
        public static readonly Guid TinyLoveMeadowDaysGyminiPlayMat = new("77777777-0024-4000-9000-000000000024");
        public static readonly Guid LEGOCityFireStationPlayset = new("77777777-0025-4000-9000-000000000025");
        public static readonly Guid VTechAlphabetTrain = new("77777777-0026-4000-9000-000000000026");
        public static readonly Guid LittleTikes45FootTrampoline = new("77777777-0027-4000-9000-000000000027");
        public static readonly Guid RazorJrLilKickScooter = new("77777777-0028-4000-9000-000000000028");
        public static readonly Guid KidKraftVintageKitchenPlayset = new("77777777-0029-4000-9000-000000000029");
        public static readonly Guid GrimmsWoodenRainbowStacker = new("77777777-002a-4000-9000-00000000002a");
        public static readonly Guid MelissaDougWoodenPegPuzzleFarmAnimals = new("77777777-002b-4000-9000-00000000002b");
        public static readonly Guid RavensburgerLabyrinthJunior = new("77777777-002c-4000-9000-00000000002c");
        public static readonly Guid KidsKaraokeMachineWithDiscoLights = new("77777777-002d-4000-9000-00000000002d");
        public static readonly Guid VTechSitToStandLearningWalker = new("77777777-002e-4000-9000-00000000002e");
        public static readonly Guid MagnaTilesClearColors32PieceSet = new("77777777-002f-4000-9000-00000000002f");
        public static readonly Guid MelissaDougWoodenAlphabetPuzzleBoard = new("77777777-0030-4000-9000-000000000030");
        public static readonly Guid Step2NaturallyPlayfulSandTable = new("77777777-0031-4000-9000-000000000031");
        public static readonly Guid PegPeregoJohnDeereGroundForceRideOnTractor = new("77777777-0032-4000-9000-000000000032");
        public static readonly Guid FisherPriceLittlePeopleFarm = new("77777777-0033-4000-9000-000000000033");
        public static readonly Guid PlanToysWoodenSortingBoard = new("77777777-0034-4000-9000-000000000034");
        public static readonly Guid Educa300PieceKidsPuzzleDinosaurs = new("77777777-0035-4000-9000-000000000035");
        public static readonly Guid CatanJunior = new("77777777-0036-4000-9000-000000000036");
        public static readonly Guid NerfRivalPartyBlasterSetX4 = new("77777777-0037-4000-9000-000000000037");
        public static readonly Guid MunchkinBathToyOrganizerSquirtersSet = new("77777777-0038-4000-9000-000000000038");
        public static readonly Guid LEGOTechnicOffRoadBuggy = new("77777777-0039-4000-9000-000000000039");
        public static readonly Guid OsmoGeniusStarterKitForIPad = new("77777777-003a-4000-9000-00000000003a");
        public static readonly Guid HedstromRainbowWaterSprinklerPlayMat = new("77777777-003b-4000-9000-00000000003b");
        public static readonly Guid Strider12SportBalanceBike = new("77777777-003c-4000-9000-00000000003c");
        public static readonly Guid PlaymobilGrandCastlePlayset = new("77777777-003d-4000-9000-00000000003d");
        public static readonly Guid LoveveryPlayKitTheBabbler = new("77777777-003e-4000-9000-00000000003e");
        public static readonly Guid JanodMagneticWoodenPuzzleBookSeasons = new("77777777-003f-4000-9000-00000000003f");
        public static readonly Guid JengaClassicWoodenBlockGame = new("77777777-0040-4000-9000-000000000040");
        public static readonly Guid PinataPartyFavorBundle = new("77777777-0041-4000-9000-000000000041");
    }

    // Toy categories. Slugs are stable, lowercase, hyphenated and unique.
    // DisplayOrder controls carousel order on the home page.
    // IconName uses PrimeIcons class names (without the "pi " prefix).
    // ColorHex is the admin-console-redesign pastel palette (10 categories, one colour each) —
    // Categories don't get created here, only backfilled onto these fixed rows (see
    // DevelopmentSeedRunner.SeedCategoriesAsync); IsVisible stays at its true default for all of them.
    public static readonly SeedCategory[] Categories =
    [
        new(new Guid("c0000004-0000-4000-9000-000000000004"), "Baby Toys",        "baby-toys",        IconName: "pi-heart",    ImageUrl: "/assets/categories/baby-toys.svg",        DisplayOrder: 1,  ColorHex: "#FFE6CC"),
        new(new Guid("c0000002-0000-4000-9000-000000000002"), "Building Blocks",  "building-blocks",  IconName: "pi-box",      ImageUrl: "/assets/categories/building-blocks.svg",  DisplayOrder: 2,  ColorHex: "#E6F2D9"),
        new(new Guid("c0000001-0000-4000-9000-000000000001"), "Educational Toys", "educational-toys", IconName: "pi-book",     ImageUrl: "/assets/categories/educational-toys.svg", DisplayOrder: 3,  ColorHex: "#F0E6FF"),
        new(new Guid("c0000003-0000-4000-9000-000000000003"), "Outdoor Toys",     "outdoor-toys",     IconName: "pi-sun",      ImageUrl: "/assets/categories/outdoor-toys.svg",     DisplayOrder: 4,  ColorHex: "#D9E8FF"),
        new(new Guid("c0000007-0000-4000-9000-000000000007"), "Ride-On Toys",     "ride-on-toys",     IconName: "pi-car",      ImageUrl: "/assets/categories/ride-on-toys.svg",     DisplayOrder: 5,  ColorHex: "#FFE0E0"),
        new(new Guid("c0000006-0000-4000-9000-000000000006"), "Pretend Play",     "pretend-play",     IconName: "pi-palette",  ImageUrl: "/assets/categories/pretend-play.svg",     DisplayOrder: 6,  ColorHex: "#E8EAFF"),
        new(new Guid("c0000009-0000-4000-9000-000000000009"), "Montessori Toys",  "montessori-toys",  IconName: "pi-leaf",     ImageUrl: "/assets/categories/montessori-toys.svg",  DisplayOrder: 7,  ColorHex: "#FFF1CC"),
        new(new Guid("c0000008-0000-4000-9000-000000000008"), "Puzzles",          "puzzles",          IconName: "pi-th-large", ImageUrl: "/assets/categories/puzzles.svg",           DisplayOrder: 8,  ColorHex: "#D9F0EC"),
        new(new Guid("c0000005-0000-4000-9000-000000000005"), "Board Games",      "board-games",      IconName: "pi-table",    ImageUrl: "/assets/categories/board-games.svg",      DisplayOrder: 9,  ColorHex: "#E6F4EE"),
        new(new Guid("c000000a-0000-4000-9000-00000000000a"), "Party Toys",       "party-toys",       IconName: "pi-gift",     ImageUrl: "/assets/categories/party-toys.svg",       DisplayOrder: 10, ColorHex: "#EDEAE3")
    ];

    // Admin console Phase 6 ("Needs category fix"): a sensible keyword set for each of the 10
    // categories above. Kept to words/phrases that are reasonably specific to the category — a
    // generic word like "wooden" or "toy" would fire on nearly everything and make the suggestion
    // noisy rather than useful.
    public static readonly SeedCategoryKeyword[] CategoryKeywords =
    [
        // Baby Toys
        new("baby-toys", "baby"),
        new("baby-toys", "infant"),
        new("baby-toys", "newborn"),
        new("baby-toys", "bouncer"),
        new("baby-toys", "teether"),
        new("baby-toys", "rattle"),

        // Building Blocks
        new("building-blocks", "lego"),
        new("building-blocks", "duplo"),
        new("building-blocks", "blocks"),
        new("building-blocks", "bricks"),
        new("building-blocks", "construction"),
        new("building-blocks", "mega bloks"),

        // Educational Toys
        new("educational-toys", "educational"),
        new("educational-toys", "stem"),
        new("educational-toys", "science"),
        new("educational-toys", "alphabet"),
        new("educational-toys", "learning"),
        new("educational-toys", "coding"),

        // Outdoor Toys
        new("outdoor-toys", "outdoor"),
        new("outdoor-toys", "slide"),
        new("outdoor-toys", "trampoline"),
        new("outdoor-toys", "sandbox"),
        new("outdoor-toys", "playhouse"),
        new("outdoor-toys", "sprinkler"),

        // Ride-On Toys
        new("ride-on-toys", "bike"),
        new("ride-on-toys", "bicycle"),
        new("ride-on-toys", "scooter"),
        new("ride-on-toys", "tricycle"),
        new("ride-on-toys", "balance bike"),
        new("ride-on-toys", "ride-on"),

        // Pretend Play
        new("pretend-play", "kitchen"),
        new("pretend-play", "dollhouse"),
        new("pretend-play", "pretend play"),
        new("pretend-play", "dress-up"),
        new("pretend-play", "tool bench"),

        // Montessori Toys
        new("montessori-toys", "montessori"),
        new("montessori-toys", "sorting"),
        new("montessori-toys", "stacking"),
        new("montessori-toys", "sensory"),

        // Puzzles
        new("puzzles", "puzzle"),
        new("puzzles", "jigsaw"),
        new("puzzles", "floor puzzle"),

        // Board Games
        new("board-games", "board game"),
        new("board-games", "card game"),
        new("board-games", "dice game"),
        new("board-games", "guess who"),

        // Party Toys
        new("party-toys", "pinata"),
        new("party-toys", "bounce house"),
        new("party-toys", "ball pit"),
        new("party-toys", "karaoke"),
        new("party-toys", "party pack")
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
            "Admin", "DoRent",
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
            UserRole.User, IsBlocked: false, PhoneNumber: "+374 91 200 003"),

        // ---- Additional owners backing the 50-listing toy-catalogue expansion (2026-08) ----
        new(
            new Guid("11111111-0009-4000-9000-000000000009"),
            DevelopmentSeedCredentials.OwnerAnahitEmail,
            "Anahit", "Grigoryan",
            UserRole.User, IsBlocked: false, PhoneNumber: "+374 94 300 001"),
        new(
            new Guid("11111111-000a-4000-9000-00000000000a"),
            DevelopmentSeedCredentials.OwnerNarekEmail,
            "Narek", "Hakobyan",
            UserRole.User, IsBlocked: false, PhoneNumber: "+374 95 300 002"),
        new(
            new Guid("11111111-000b-4000-9000-00000000000b"),
            DevelopmentSeedCredentials.OwnerLilitEmail,
            "Lilit", "Sargsyan",
            UserRole.User, IsBlocked: false, PhoneNumber: "+374 96 300 003"),
        new(
            new Guid("11111111-000c-4000-9000-00000000000c"),
            DevelopmentSeedCredentials.OwnerDavitEmail,
            "Davit", "Petrosyan",
            UserRole.User, IsBlocked: false, PhoneNumber: "+374 97 300 004"),
        new(
            new Guid("11111111-000d-4000-9000-00000000000d"),
            DevelopmentSeedCredentials.OwnerMariamEmail,
            "Mariam", "Avetisyan",
            UserRole.User, IsBlocked: false, PhoneNumber: "+374 98 300 005")
    ];

    // 10 toy listings (7 Approved, 2 PendingApproval, 1 Rejected), all owned by demo owner, Yerevan-focused.
    // Coordinates are deliberately spread across 8 of the 12 Yerevan districts (Kentron, Arabkir,
    // Nork-Marash, Malatia-Sebastia, Nor Nork, Davtashen, Shengavit, Erebuni) so the district
    // point-in-polygon lookup (P1-4) and the map/district UI have more than one district to render.
    // The Gyumri listing (Birthday Party Toy Pack) is intentionally outside every Yerevan district
    // boundary — it exercises the "no district match, DistrictId stays null" path. Every
    // coordinate here was verified against DistrictBoundaryProvider.FindDistrictCode before being
    // baked in (see the district assignment comment on each listing needing it, where non-obvious).
    public static readonly SeedListing[] Listings =
    [
        new(
            ListingIds.LegoDuploStarterSet,
            "LEGO Duplo Starter Set",
            "Classic LEGO Duplo starter set with 80+ chunky pieces. Sanitized between rentals and stored in a sealed box.",
            "building-blocks", DevelopmentSeedCredentials.OwnerEmail,
            2500m, "AMD", "Armenia", "Yerevan", "8 Saryan St",
            40.1856m, 44.5126m,
            ListingStatus.Approved, CreatedDaysAgo: 14, UpdatedDaysAgo: 2,
            AgeFromMonths: 18, AgeToMonths: 60,
            Condition: "Excellent",
            HygieneNotes: "Wiped down with child-safe disinfectant after every return. Complete piece count verified.",
            SafetyNotes: "All pieces are large enough to comply with EN 71-1 small-parts requirements. No loose batteries.",
            DepositAmount: 12000m),
        new(
            ListingIds.MontessoriWoodenToySet,
            "Montessori Wooden Toy Set",
            "Six-piece natural wood Montessori set: shape sorter, stacking rings, threading beads, peg board and counting bars.",
            "montessori-toys", DevelopmentSeedCredentials.OwnerEmail,
            3500m, "AMD", "Armenia", "Yerevan", "12 Kasyan St",
            40.2140m, 44.5220m,
            ListingStatus.Approved, CreatedDaysAgo: 12, UpdatedDaysAgo: 3,
            AgeFromMonths: 24, AgeToMonths: 72,
            Condition: "Like new",
            HygieneNotes: "Wood pieces wiped with a damp cloth and left to fully air-dry between rentals.",
            SafetyNotes: "Smooth, splinter-free finish. Non-toxic water-based stain. No magnets, no detachable small parts.",
            DepositAmount: 14000m),
        new(
            ListingIds.BabyActivityGym,
            "Baby Activity Gym",
            "Padded baby activity gym with detachable hanging toys, a mirror, a textured teether and a soft rattle.",
            "baby-toys", DevelopmentSeedCredentials.OwnerEmail,
            2000m, "AMD", "Armenia", "Yerevan", "19 Isahakyan St",
            40.1887m, 44.5134m,
            ListingStatus.Approved, CreatedDaysAgo: 10, UpdatedDaysAgo: 1,
            AgeFromMonths: 0, AgeToMonths: 12,
            Condition: "Excellent",
            HygieneNotes: "Removable mat is machine-washed at 60 °C between rentals. Hanging toys are surface-sanitized.",
            SafetyNotes: "All attachments are double-stitched and torque-tested. Suitable for supervised tummy time.",
            DepositAmount: 10000m),
        new(
            ListingIds.KidsBalanceBike,
            "Kids Balance Bike",
            "Lightweight 12-inch balance bike with adjustable seat (30-42 cm) and puncture-resistant tyres.",
            "ride-on-toys", DevelopmentSeedCredentials.OwnerEmail,
            3000m, "AMD", "Armenia", "Yerevan", "5 Titanyan St",
            40.1810m, 44.5370m,
            ListingStatus.Approved, CreatedDaysAgo: 9, UpdatedDaysAgo: 2,
            AgeFromMonths: 24, AgeToMonths: 60,
            Condition: "Good",
            HygieneNotes: "Frame and grips wiped with disinfectant; saddle cover wiped with antibacterial spray.",
            SafetyNotes: "Helmet not included. Owner recommends a fitted helmet and supervised use on flat surfaces.",
            DepositAmount: 16000m),
        new(
            ListingIds.OutdoorBackyardSlide,
            "Outdoor Backyard Slide",
            "Stable plastic backyard slide, ~1.2 m climb. Easy to wipe down. Great for small gardens and play days.",
            "outdoor-toys", DevelopmentSeedCredentials.OwnerEmail,
            4000m, "AMD", "Armenia", "Yerevan", "22 Gai Ave",
            40.1745m, 44.4475m,
            ListingStatus.Approved, CreatedDaysAgo: 8, UpdatedDaysAgo: 1,
            AgeFromMonths: 18, AgeToMonths: 72,
            Condition: "Good",
            HygieneNotes: "Surfaces wiped with mild soap and water after every rental, then dried.",
            SafetyNotes: "Must be placed on level ground. Owner provides anti-slip pads. Max user weight 25 kg.",
            DepositAmount: 20000m),
        new(
            ListingIds.ChildrensPuzzleBundle,
            "Children's Puzzle Bundle",
            "Bundle of four wooden puzzles (12, 24, 48 and 60 pieces). All pieces verified present before pickup.",
            "puzzles", DevelopmentSeedCredentials.OwnerEmail,
            1500m, "AMD", "Armenia", "Yerevan", "18 Pushkin St",
            40.1831m, 44.5100m,
            ListingStatus.Approved, CreatedDaysAgo: 7, UpdatedDaysAgo: 2,
            AgeFromMonths: 36, AgeToMonths: 96,
            Condition: "Like new",
            HygieneNotes: "Pieces wiped with a slightly damp cloth and air-dried between rentals.",
            SafetyNotes: "Smallest pieces are above the 3-year-old small-parts threshold. Not recommended under 36 months.",
            DepositAmount: 8000m),
        new(
            ListingIds.ToyKitchenSet,
            "Wooden Toy Kitchen Set",
            "Wooden play kitchen with stove, sink, oven door and 20 accessories (utensils, pots, play food).",
            "pretend-play", DevelopmentSeedCredentials.OwnerEmail,
            3500m, "AMD", "Armenia", "Yerevan", "5 Republic Square",
            40.1776m, 44.5126m,
            ListingStatus.Approved, CreatedDaysAgo: 6, UpdatedDaysAgo: 1,
            AgeFromMonths: 30, AgeToMonths: 96,
            Condition: "Excellent",
            HygieneNotes: "Wood surfaces wiped with food-safe cleaner; small accessories washed in soapy water.",
            SafetyNotes: "Rounded edges. No glass, no magnets, no detachable small parts under 36 months.",
            DepositAmount: 16000m),

        // ---- PendingApproval (admin moderation queue) ----
        new(
            ListingIds.BoardGameFamilyBundle,
            "Board Game Family Bundle",
            "Family game-night bundle: three age-appropriate board games covering memory, strategy and cooperation.",
            "board-games", DevelopmentSeedCredentials.OwnerEmail,
            2000m, "AMD", "Armenia", "Yerevan", "12 Komitas Ave",
            40.2016m, 44.4915m,
            ListingStatus.PendingApproval, CreatedDaysAgo: 3, UpdatedDaysAgo: 1,
            AgeFromMonths: 48, AgeToMonths: 144,
            Condition: "Like new",
            HygieneNotes: "Cards and pieces wiped down between rentals; boxes inspected for completeness.",
            SafetyNotes: "Contains small parts; not suitable under 36 months without supervision.",
            DepositAmount: 10000m),
        new(
            ListingIds.BirthdayPartyToyPack,
            "Birthday Party Toy Pack",
            "Party toy pack: bean bags, foam darts, ring toss, pin-the-tail and a soft ball pool (50 balls).",
            "party-toys", DevelopmentSeedCredentials.OwnerEmail,
            5000m, "AMD", "Armenia", "Gyumri", "25 Abovyan St",
            40.7850m, 43.8453m,
            ListingStatus.PendingApproval, CreatedDaysAgo: 2, UpdatedDaysAgo: 0,
            AgeFromMonths: 36, AgeToMonths: 144,
            Condition: "Good",
            HygieneNotes: "Balls and fabric items washed; foam darts wiped with antibacterial wipes.",
            SafetyNotes: "Foam darts only. No projectile toys. Adult supervision recommended.",
            DepositAmount: 12000m),

        // ---- Rejected ----
        new(
            ListingIds.SoftPlayFoamSet,
            "Soft Play Foam Set",
            "Foam soft-play set submitted for moderation testing. Will be rejected because hygiene notes were left empty.",
            "educational-toys", DevelopmentSeedCredentials.OwnerEmail,
            3500m, "AMD", "Armenia", "Yerevan", "3 Mashtots Ave",
            40.1833m, 44.5150m,
            ListingStatus.Rejected, CreatedDaysAgo: 6, UpdatedDaysAgo: 5,
            AgeFromMonths: 12, AgeToMonths: 60,
            Condition: "Used",
            HygieneNotes: null,
            SafetyNotes: null,
            DepositAmount: 10000m,
            RejectionReason: "Hygiene notes are required. Please describe how the item is cleaned between rentals."),

        // ---- Additional approved listings owned by demo_owner@toyrent.am (listings 8–12 approved) ----
        new(
            ListingIds.StemScienceDiscoveryKit,
            "STEM Science Discovery Kit",
            "Hands-on science kit with 20+ experiments: volcano, crystal growing, slime, and simple circuit activities. All chemicals are child-safe and pre-measured.",
            "educational-toys", DevelopmentSeedCredentials.DemoOwnerEmail,
            3000m, "AMD", "Armenia", "Yerevan", "8 Sarmen St",
            40.1840m, 44.5720m,
            ListingStatus.Approved, CreatedDaysAgo: 11, UpdatedDaysAgo: 2,
            AgeFromMonths: 60, AgeToMonths: 144,
            Condition: "Excellent",
            HygieneNotes: "Single-use chemical sachets replaced after each rental. Trays and tools washed with soapy water.",
            SafetyNotes: "Adult supervision required for all experiments. No open flames. Includes safety goggles.",
            DepositAmount: 8000m),
        new(
            ListingIds.ClassicBoardGameTrio,
            "Classic Board Game Trio",
            "Three timeless board games in one bundle: Snakes & Ladders, Ludo, and a 100-piece junior jigsaw. All pieces verified complete.",
            "board-games", DevelopmentSeedCredentials.DemoOwnerEmail,
            2000m, "AMD", "Armenia", "Yerevan", "14 Aygestani St",
            40.2215m, 44.4795m,
            ListingStatus.Approved, CreatedDaysAgo: 9, UpdatedDaysAgo: 1,
            AgeFromMonths: 48, AgeToMonths: 144,
            Condition: "Like new",
            HygieneNotes: "Cards and tokens wiped with a dry cloth before return; boxes sealed with elastic for storage.",
            SafetyNotes: "Contains small pieces; not suitable for children under 3 years without supervision.",
            DepositAmount: 6000m),
        new(
            ListingIds.PartyFunActivityPack,
            "Party Fun Activity Pack",
            "Complete party activity kit: parachute play cloth, bean bags, hula hoops (×2), jump rope, and a set of colourful cones. Perfect for birthdays and group play.",
            "party-toys", DevelopmentSeedCredentials.DemoOwnerEmail,
            4500m, "AMD", "Armenia", "Yerevan", "22 Azatutyan Ave",
            40.2102m, 44.4997m,
            ListingStatus.Approved, CreatedDaysAgo: 7, UpdatedDaysAgo: 1,
            AgeFromMonths: 36, AgeToMonths: 144,
            Condition: "Good",
            HygieneNotes: "Fabric items machine-washed after every rental. Hard plastic items wiped with antibacterial spray.",
            SafetyNotes: "Parachute activity requires adult supervision. Clear a flat open area of at least 4 × 4 m.",
            DepositAmount: 12000m),
        new(
            ListingIds.WoodenTrainSet,
            "Wooden Train Set & Track (56 pcs)",
            "56-piece wooden train set with figure-of-eight track, bridges, tunnels, a station, two engines and six carriages. Compatible with major wooden-rail brands.",
            "building-blocks", DevelopmentSeedCredentials.DemoOwnerEmail,
            3500m, "AMD", "Armenia", "Yerevan", "30 Manandyan St",
            40.1230m, 44.4770m,
            ListingStatus.Approved, CreatedDaysAgo: 5, UpdatedDaysAgo: 1,
            AgeFromMonths: 24, AgeToMonths: 84,
            Condition: "Excellent",
            HygieneNotes: "Track pieces and rolling stock wiped with a damp cloth and dried before packing. Piece count verified.",
            SafetyNotes: "No small detachable parts below 3-year-old threshold. Supervised use recommended under 24 months.",
            DepositAmount: 14000m),
        new(
            ListingIds.KidsArtEasel,
            "Kids Double-Sided Art Easel",
            "Height-adjustable double-sided easel: whiteboard on one side, blackboard on the other, with a paper roll holder. Includes chalk, eraser, and 3 dry-erase markers.",
            "pretend-play", DevelopmentSeedCredentials.DemoOwnerEmail,
            2500m, "AMD", "Armenia", "Yerevan", "12 Ohanyan St",
            40.1495m, 44.5470m,
            ListingStatus.Approved, CreatedDaysAgo: 4, UpdatedDaysAgo: 0,
            AgeFromMonths: 24, AgeToMonths: 96,
            Condition: "Like new",
            HygieneNotes: "Whiteboard and blackboard surfaces wiped clean before handover. Markers capped and tested.",
            SafetyNotes: "Child-safe, non-toxic chalk and markers. Easel folds flat for transport; locking pins provided.",
            DepositAmount: 8000m),

        // ==================================================================================
        // ---- 50-listing toy-catalogue expansion (2026-08) ----
        // 5 listings per category (all 10 existing category slugs), owners spread across the
        // 5 new owners (Anahit/Narek/Lilit/Davit/Mariam) plus demo_owner@toyrent.am (~8-9 each).
        // Status split: 44 Approved / 4 PendingApproval / 2 Rejected.
        // Coordinates spread across all 12 Yerevan districts (4-5 each), each one verified
        // against DistrictBoundaryProvider.FindDistrictCode -- see DistrictSeedDataTests for the
        // per-listing expected-district table this block is checked against.
        // ==================================================================================
        new(
            ListingIds.FisherPrice4In1OceanWondersBouncer,
            "Fisher-Price 4-in-1 Ocean Wonders Bouncer",
            "Vibrating baby bouncer with a removable ocean-themed toy bar, three recline positions and a machine-washable seat pad. Includes calming vibration and two volume settings.",
            "baby-toys", DevelopmentSeedCredentials.DemoOwnerEmail,
            2200m, "AMD", "Armenia", "Yerevan", "14 Halabyan St",
            40.2068m, 44.4417m,
            ListingStatus.Approved, CreatedDaysAgo: 8, UpdatedDaysAgo: 5,
            AgeFromMonths: 0, AgeToMonths: 9,
            Condition: "Excellent",
            HygieneNotes: "Seat pad and toy bar wiped with baby-safe disinfectant and machine-washed at 40C between rentals.",
            SafetyNotes: "5-point harness included. Vibration unit runs on batteries only, no mains cord near the seat. Not for unsupervised sleep.",
            DepositAmount: 9000m),
        new(
            ListingIds.LEGOClassicCreativeBricksBox500Pcs,
            "LEGO Classic Creative Bricks Box (500 pcs)",
            "500-piece LEGO Classic set in assorted colours and shapes, stored in the original sorting box. Piece count verified after every return.",
            "building-blocks", DevelopmentSeedCredentials.OwnerAnahitEmail,
            3200m, "AMD", "Armenia", "Yerevan", "33 Komitas Ave",
            40.2207m, 44.5253m,
            ListingStatus.Approved, CreatedDaysAgo: 15, UpdatedDaysAgo: 9,
            AgeFromMonths: 48, AgeToMonths: 144,
            Condition: "Excellent",
            HygieneNotes: "Bricks run through a mesh-bag wash in warm soapy water and air-dried on a towel before repacking.",
            SafetyNotes: "Standard LEGO brick sizes; not recommended for children under 3 due to the small-parts risk printed on the box.",
            DepositAmount: 15000m),
        new(
            ListingIds.LeapFrogLeapStartInteractiveLearningSystem,
            "LeapFrog LeapStart Interactive Learning System",
            "Interactive learning console with a stylus pen and two activity books covering letters, numbers and early reading.",
            "educational-toys", DevelopmentSeedCredentials.OwnerNarekEmail,
            2800m, "AMD", "Armenia", "Yerevan", "22 Acharyan St",
            40.2233m, 44.5641m,
            ListingStatus.Approved, CreatedDaysAgo: 22, UpdatedDaysAgo: 13,
            AgeFromMonths: 24, AgeToMonths: 84,
            Condition: "Excellent",
            HygieneNotes: "Console and stylus wiped with antibacterial wipes; book pages spot-cleaned, no sticky residue.",
            SafetyNotes: "Runs on batteries only, no charging cable included. Volume capped at a child-safe level.",
            DepositAmount: 12000m),
        new(
            ListingIds.SmobyOutdoorPlayhouse,
            "Smoby Outdoor Playhouse",
            "Large plastic outdoor playhouse (approx. 1.5 x 1.3 m) with a working door, windows and a mailbox. Assembly and disassembly included at pickup/return.",
            "outdoor-toys", DevelopmentSeedCredentials.OwnerLilitEmail,
            8500m, "AMD", "Armenia", "Yerevan", "Davtashen 3rd District, Bldg 12",
            40.2243m, 44.4771m,
            ListingStatus.Approved, CreatedDaysAgo: 29, UpdatedDaysAgo: 17,
            AgeFromMonths: 24, AgeToMonths: 96,
            Condition: "Good",
            HygieneNotes: "Interior and exterior panels hosed down and scrubbed with mild detergent, then fully air-dried.",
            SafetyNotes: "Anchored with ground stakes for stability; all edges are rounded plastic with no sharp seams.",
            DepositAmount: 32000m),
        new(
            ListingIds.LittleTikesCozyCoupe,
            "Little Tikes Cozy Coupe",
            "Classic ride-on car with a working horn, opening doors and a floorboard storage compartment. No pedals -- foot-to-floor propulsion.",
            "ride-on-toys", DevelopmentSeedCredentials.OwnerDavitEmail,
            3000m, "AMD", "Armenia", "Yerevan", "145 Arshakunyats Ave",
            40.1540m, 44.5420m,
            ListingStatus.Approved, CreatedDaysAgo: 36, UpdatedDaysAgo: 21,
            AgeFromMonths: 18, AgeToMonths: 48,
            Condition: "Good",
            HygieneNotes: "Seat and steering wheel wiped with disinfectant spray; interior vacuumed of dust and crumbs.",
            SafetyNotes: "No sharp edges on the body shell. Recommended for supervised outdoor or driveway use only.",
            DepositAmount: 12000m),
        new(
            ListingIds.MelissaDougWoodenDollhouse,
            "Melissa & Doug Wooden Dollhouse",
            "Three-storey wooden dollhouse with 12 rooms of furniture and a working front door and windows.",
            "pretend-play", DevelopmentSeedCredentials.OwnerMariamEmail,
            4500m, "AMD", "Armenia", "Yerevan", "44 Zoravar Andranik Ave",
            40.2158m, 44.5325m,
            ListingStatus.Approved, CreatedDaysAgo: 43, UpdatedDaysAgo: 25,
            AgeFromMonths: 36, AgeToMonths: 96,
            Condition: "Excellent",
            HygieneNotes: "Furniture pieces wiped individually with a damp cloth; house frame dusted and wiped down.",
            SafetyNotes: "No small removable roof or wall pieces. Furniture edges are sanded smooth.",
            DepositAmount: 18000m),
        new(
            ListingIds.HapePoundTapBench,
            "Hape Pound & Tap Bench",
            "Wooden pound-and-tap bench with a xylophone base, wooden mallet and five colourful balls.",
            "montessori-toys", DevelopmentSeedCredentials.DemoOwnerEmail,
            2000m, "AMD", "Armenia", "Yerevan", "27 Abovyan St",
            40.1833m, 44.5067m,
            ListingStatus.Approved, CreatedDaysAgo: 50, UpdatedDaysAgo: 29,
            AgeFromMonths: 10, AgeToMonths: 36,
            Condition: "Good",
            HygieneNotes: "Balls and mallet washed in warm soapy water; bench surface wiped and dried before each rental.",
            SafetyNotes: "Mallet has a rounded head and thick handle sized for toddler grip, no splinter risk.",
            DepositAmount: 8000m),
        new(
            ListingIds.Djeco100PieceFloorPuzzleFamilyReunion,
            "Djeco 100-Piece Floor Puzzle - Family Reunion",
            "Large-format 100-piece floor puzzle (assembled size 70 x 50 cm) with vivid illustrated artwork.",
            "puzzles", DevelopmentSeedCredentials.OwnerAnahitEmail,
            1400m, "AMD", "Armenia", "Yerevan", "19 Sebastia St",
            40.1788m, 44.4421m,
            ListingStatus.Rejected, CreatedDaysAgo: 9, UpdatedDaysAgo: 8,
            AgeFromMonths: 36, AgeToMonths: 84,
            Condition: "Good",
            HygieneNotes: "Pieces wiped with a dry cloth; storage box interior vacuumed of dust between rentals.",
            SafetyNotes: "Thick cardboard pieces are larger than standard puzzle pieces, reducing choking risk.",
            DepositAmount: 5000m,
            RejectionReason: "Photos show only the closed box, not the assembled puzzle -- please add a photo of the completed puzzle so renters can confirm all pieces are present."),
        new(
            ListingIds.HasbroGuessWhoClassic,
            "Hasbro Guess Who? Classic",
            "Classic two-player guessing game with 24 character cards and two flip-panel boards, all pieces accounted for.",
            "board-games", DevelopmentSeedCredentials.OwnerNarekEmail,
            1500m, "AMD", "Armenia", "Yerevan", "Nork 3rd Microdistrict, Bldg 8",
            40.1854m, 44.5337m,
            ListingStatus.Approved, CreatedDaysAgo: 4, UpdatedDaysAgo: 1,
            AgeFromMonths: 72, AgeToMonths: 144,
            Condition: "Good",
            HygieneNotes: "Character cards and flip panels wiped with a dry cloth; hinges checked for smooth movement.",
            SafetyNotes: "No small loose parts beyond the character cards, which exceed choking-hazard size.",
            DepositAmount: 6000m),
        new(
            ListingIds.LittleTikesInflatableBounceHouse,
            "Little Tikes Inflatable Bounce House",
            "Compact inflatable bounce house (approx. 2.5 x 2.5 m) with an electric blower included, sets up in a garden or large room.",
            "party-toys", DevelopmentSeedCredentials.OwnerLilitEmail,
            9000m, "AMD", "Armenia", "Yerevan", "Nor Nork 2nd Microdistrict, Bldg 15",
            40.1885m, 44.5682m,
            ListingStatus.Approved, CreatedDaysAgo: 11, UpdatedDaysAgo: 3,
            AgeFromMonths: 36, AgeToMonths: 144,
            Condition: "Good",
            HygieneNotes: "Interior and exterior surfaces wiped with a diluted disinfectant solution and fully air-dried before folding.",
            SafetyNotes: "Blower has a safety-rated cord and auto-shutoff. Maximum occupancy and weight limits are listed on the netting.",
            DepositAmount: 35000m),
        new(
            ListingIds.ChiccoBabySensesActivityGym,
            "Chicco Baby Senses Activity Gym",
            "Padded activity gym with an arched frame, five hanging toys, a crinkly cloud and a soft-light star that plays lullabies.",
            "baby-toys", DevelopmentSeedCredentials.OwnerDavitEmail,
            2400m, "AMD", "Armenia", "Yerevan", "Nubarashen 1st Microdistrict, Bldg 6",
            40.0888m, 44.5224m,
            ListingStatus.Approved, CreatedDaysAgo: 18, UpdatedDaysAgo: 3,
            AgeFromMonths: 0, AgeToMonths: 12,
            Condition: "Like new",
            HygieneNotes: "Play mat machine-washed at 60C; hanging toys hand-washed and air-dried between rentals.",
            SafetyNotes: "Hanging toys are securely clipped and too large to be a choking hazard. Battery compartment for the light star is screw-locked.",
            DepositAmount: 10000m),
        new(
            ListingIds.MegaBloksFirstBuildersBigBuildingBag80Pcs,
            "Mega Bloks First Builders Big Building Bag (80 pcs)",
            "80 oversized, toddler-safe blocks in a zippered carry bag, compatible with other First Builders sets.",
            "building-blocks", DevelopmentSeedCredentials.OwnerMariamEmail,
            2000m, "AMD", "Armenia", "Yerevan", "88 Bagratunyats Ave",
            40.1268m, 44.4711m,
            ListingStatus.Approved, CreatedDaysAgo: 25, UpdatedDaysAgo: 14,
            AgeFromMonths: 12, AgeToMonths: 60,
            Condition: "Good",
            HygieneNotes: "Blocks wiped with a damp cloth and child-safe disinfectant spray; carry bag wiped down and air-dried.",
            SafetyNotes: "Blocks are large-format and exceed small-parts size limits, safe for supervised toddler play.",
            DepositAmount: 8000m),
        new(
            ListingIds.LearningResourcesCodingCrittersRangerZip,
            "Learning Resources Coding Critters Ranger & Zip",
            "Screen-free coding toy set: a robot pet and code cards that teach sequencing through play.",
            "educational-toys", DevelopmentSeedCredentials.DemoOwnerEmail,
            3000m, "AMD", "Armenia", "Yerevan", "27 Gubkin St",
            40.1968m, 44.4497m,
            ListingStatus.Approved, CreatedDaysAgo: 32, UpdatedDaysAgo: 25,
            AgeFromMonths: 48, AgeToMonths: 96,
            Condition: "Like new",
            HygieneNotes: "Robot shell wiped with disinfectant spray; code cards wiped clean and checked for bent corners.",
            SafetyNotes: "Small code cards are choke-hazard sized -- recommended for 4 years and up with supervision.",
            DepositAmount: 12000m),
        new(
            ListingIds.IntexInflatableKiddiePoolOceanPlayCenter,
            "Intex Inflatable Kiddie Pool (Ocean Play Center)",
            "Inflatable pool with a shaded canopy, built-in slide and ring toss game, holds approx. 190 L.",
            "outdoor-toys", DevelopmentSeedCredentials.OwnerAnahitEmail,
            3000m, "AMD", "Armenia", "Yerevan", "18 Vratsakan St",
            40.2117m, 44.5153m,
            ListingStatus.Approved, CreatedDaysAgo: 39, UpdatedDaysAgo: 36,
            AgeFromMonths: 12, AgeToMonths: 72,
            Condition: "Good",
            HygieneNotes: "Fully deflated, scrubbed with pool-safe disinfectant and left to air-dry before each rental.",
            SafetyNotes: "Adult supervision required at all times near water. Repair patch kit included in case of small punctures.",
            DepositAmount: 10000m),
        new(
            ListingIds.RadioFlyerClassicRedTricycle,
            "Radio Flyer Classic Red Tricycle",
            "Steel-frame tricycle with a rear step-plate for a parent push handle and an adjustable seat.",
            "ride-on-toys", DevelopmentSeedCredentials.OwnerNarekEmail,
            2500m, "AMD", "Armenia", "Yerevan", "5 Davit Anhaght St",
            40.2143m, 44.5731m,
            ListingStatus.PendingApproval, CreatedDaysAgo: 5, UpdatedDaysAgo: 3,
            AgeFromMonths: 24, AgeToMonths: 60,
            Condition: "Excellent",
            HygieneNotes: "Frame and handlebars wiped with disinfectant; seat cover wiped separately with antibacterial spray.",
            SafetyNotes: "Push handle allows parent control at low speeds. Pedals have a non-slip rubber tread.",
            DepositAmount: 10000m),
        new(
            ListingIds.Step2FixerUpperToolBench,
            "Step2 Fixer Upper Tool Bench",
            "Plastic tool workbench with 20+ pretend tools, a working vice and realistic drill sounds.",
            "pretend-play", DevelopmentSeedCredentials.OwnerLilitEmail,
            3200m, "AMD", "Armenia", "Yerevan", "Davtashen 4th District, Bldg 5",
            40.2183m, 44.4831m,
            ListingStatus.Approved, CreatedDaysAgo: 53, UpdatedDaysAgo: 5,
            AgeFromMonths: 24, AgeToMonths: 72,
            Condition: "Good",
            HygieneNotes: "Tools and bench surface wiped with antibacterial spray; battery-powered drill sound module checked.",
            SafetyNotes: "All tools are soft plastic with rounded tips. Vice mechanism has a finger-pinch guard.",
            DepositAmount: 13000m),
        new(
            ListingIds.MelissaDougShapeSortingCube,
            "Melissa & Doug Shape Sorting Cube",
            "Wooden shape-sorting cube with 12 chunky shapes and a lift-off lid for storage.",
            "montessori-toys", DevelopmentSeedCredentials.OwnerDavitEmail,
            1400m, "AMD", "Armenia", "Yerevan", "8 Nor Aresh St",
            40.1430m, 44.5520m,
            ListingStatus.Approved, CreatedDaysAgo: 60, UpdatedDaysAgo: 9,
            AgeFromMonths: 12, AgeToMonths: 36,
            Condition: "Excellent",
            HygieneNotes: "Shapes wiped with a damp cloth and mild soap; cube interior vacuumed of dust between rentals.",
            SafetyNotes: "Shapes are large and rounded, exceeding standard small-parts thresholds for toddlers.",
            DepositAmount: 5500m),
        new(
            ListingIds.Ravensburger200PieceDisneyPuzzle,
            "Ravensburger 200-Piece Disney Puzzle",
            "200-piece children's jigsaw featuring classic Disney characters, glare-free finish, complete piece count verified.",
            "puzzles", DevelopmentSeedCredentials.OwnerMariamEmail,
            1200m, "AMD", "Armenia", "Yerevan", "9 Kanaker St",
            40.2068m, 44.5415m,
            ListingStatus.Approved, CreatedDaysAgo: 7, UpdatedDaysAgo: 2,
            AgeFromMonths: 60, AgeToMonths: 120,
            Condition: "Like new",
            HygieneNotes: "Pieces wiped with a lightly damp cloth and fully dried before boxing to prevent warping.",
            SafetyNotes: "Standard jigsaw piece size, recommended for ages 5 and up without supervision needed.",
            DepositAmount: 5000m),
        new(
            ListingIds.HabaMyVeryFirstGamesOrchardCompare,
            "Haba My Very First Games - Orchard Compare",
            "Cooperative matching game for young children with a wooden spinner and chunky wooden fruit pieces.",
            "board-games", DevelopmentSeedCredentials.DemoOwnerEmail,
            1600m, "AMD", "Armenia", "Yerevan", "54 Tumanyan St",
            40.1763m, 44.5137m,
            ListingStatus.Approved, CreatedDaysAgo: 14, UpdatedDaysAgo: 13,
            AgeFromMonths: 36, AgeToMonths: 72,
            Condition: "Excellent",
            HygieneNotes: "Wooden fruit pieces washed in warm soapy water; game box interior wiped clean.",
            SafetyNotes: "Fruit pieces are large and rounded, designed specifically for the 3+ age range to avoid choking risk.",
            DepositAmount: 6000m),
        new(
            ListingIds.IntexBallPitWith100Balls,
            "Intex Ball Pit with 100 Balls",
            "Round inflatable ball pit (120 cm diameter) that comes complete with 100 phthalate-free plastic balls.",
            "party-toys", DevelopmentSeedCredentials.OwnerAnahitEmail,
            3000m, "AMD", "Armenia", "Yerevan", "44 Raffi St",
            40.1708m, 44.4511m,
            ListingStatus.Approved, CreatedDaysAgo: 21, UpdatedDaysAgo: 3,
            AgeFromMonths: 12, AgeToMonths: 72,
            Condition: "Good",
            HygieneNotes: "Balls washed in a mesh laundry bag with mild detergent; pit walls wiped and fully dried between rentals.",
            SafetyNotes: "Balls are sized above the choking-hazard limit. Pit walls have no sharp seams.",
            DepositAmount: 12000m),
        new(
            ListingIds.TinyLoveMeadowDaysGyminiPlayMat,
            "Tiny Love Meadow Days Gymini Play Mat",
            "Foldable tummy-time play mat with a detachable arch, four soft toys and a fold-up carry bag for storage.",
            "baby-toys", DevelopmentSeedCredentials.OwnerNarekEmail,
            2000m, "AMD", "Armenia", "Yerevan", "14 Marash St",
            40.1774m, 44.5417m,
            ListingStatus.Approved, CreatedDaysAgo: 28, UpdatedDaysAgo: 21,
            AgeFromMonths: 0, AgeToMonths: 12,
            Condition: "Good",
            HygieneNotes: "Mat surface wiped with a damp cloth and mild soap; fabric toys washed on a gentle cycle.",
            SafetyNotes: "Arch legs are wide-set and stable on flat floors. All attached toys are stitched, not glued.",
            DepositAmount: 8000m),
        new(
            ListingIds.LEGOCityFireStationPlayset,
            "LEGO City Fire Station Playset",
            "Fire Station playset with a fire engine, helicopter, four minifigures and a working ladder function. All 250+ pieces checked against the parts list before pickup.",
            "building-blocks", DevelopmentSeedCredentials.OwnerLilitEmail,
            4200m, "AMD", "Armenia", "Yerevan", "Nor Nork 5th Microdistrict, Bldg 3",
            40.1795m, 44.5772m,
            ListingStatus.Approved, CreatedDaysAgo: 35, UpdatedDaysAgo: 4,
            AgeFromMonths: 60, AgeToMonths: 144,
            Condition: "Excellent",
            HygieneNotes: "Minifigures and larger pieces wiped with disinfectant wipes; small connectors washed in a mesh bag.",
            SafetyNotes: "Contains small parts and is not suitable for children under 5. Ladder mechanism checked for sharp edges before each rental.",
            DepositAmount: 20000m),
        new(
            ListingIds.VTechAlphabetTrain,
            "VTech Alphabet Train",
            "Pull-along alphabet train with 26 letter blocks that snap onto the carriages.",
            "educational-toys", DevelopmentSeedCredentials.OwnerDavitEmail,
            1800m, "AMD", "Armenia", "Yerevan", "Nubarashen 2nd Microdistrict, Bldg 2",
            40.0838m, 44.5274m,
            ListingStatus.Approved, CreatedDaysAgo: 42, UpdatedDaysAgo: 15,
            AgeFromMonths: 12, AgeToMonths: 36,
            Condition: "Good",
            HygieneNotes: "Letter blocks washed in warm soapy water; train body wiped with a damp cloth and dried fully.",
            SafetyNotes: "Blocks are large enough to meet toddler small-parts safety limits. Pull string is under 30 cm.",
            DepositAmount: 7000m),
        new(
            ListingIds.LittleTikes45FootTrampoline,
            "Little Tikes 4.5-Foot Trampoline",
            "Toddler trampoline with a padded safety enclosure net and non-slip frame pads. Fits comfortably in a small yard.",
            "outdoor-toys", DevelopmentSeedCredentials.OwnerMariamEmail,
            6500m, "AMD", "Armenia", "Yerevan", "17 Manandyan St",
            40.1178m, 44.4801m,
            ListingStatus.PendingApproval, CreatedDaysAgo: 2, UpdatedDaysAgo: 1,
            AgeFromMonths: 36, AgeToMonths: 96,
            Condition: "Excellent",
            HygieneNotes: "Jump mat and enclosure net wiped down with disinfectant spray and left to dry in the sun.",
            SafetyNotes: "Frame pads and net checked for tears before every rental. Maximum one child at a time, adult supervision required.",
            DepositAmount: 25000m),
        new(
            ListingIds.RazorJrLilKickScooter,
            "Razor Jr. Lil' Kick Scooter",
            "Three-wheeled kick scooter with a lean-to-steer frame and an extra-wide footboard for balance.",
            "ride-on-toys", DevelopmentSeedCredentials.DemoOwnerEmail,
            1800m, "AMD", "Armenia", "Yerevan", "41 Halabyan St",
            40.2048m, 44.4527m,
            ListingStatus.Approved, CreatedDaysAgo: 56, UpdatedDaysAgo: 37,
            AgeFromMonths: 24, AgeToMonths: 60,
            Condition: "Good",
            HygieneNotes: "Footboard and handlebar grips wiped with antibacterial wipes after every rental.",
            SafetyNotes: "Low centre of gravity for stability. Helmet not included -- owner recommends one for outdoor use.",
            DepositAmount: 7000m),
        new(
            ListingIds.KidKraftVintageKitchenPlayset,
            "KidKraft Vintage Kitchen Playset",
            "Wooden play kitchen with an oven, stovetop, sink and icebox, plus 25 kitchen accessories included.",
            "pretend-play", DevelopmentSeedCredentials.OwnerAnahitEmail,
            5500m, "AMD", "Armenia", "Yerevan", "52 Komitas Ave",
            40.2217m, 44.5183m,
            ListingStatus.Approved, CreatedDaysAgo: 3, UpdatedDaysAgo: 3,
            AgeFromMonths: 24, AgeToMonths: 96,
            Condition: "Excellent",
            HygieneNotes: "Wood surfaces wiped with food-safe cleaner; plastic accessories washed in soapy water and air-dried.",
            SafetyNotes: "No glass or breakable parts. Oven and icebox doors have soft-close hinges.",
            DepositAmount: 22000m),
        new(
            ListingIds.GrimmsWoodenRainbowStacker,
            "Grimm's Wooden Rainbow Stacker",
            "12-piece wooden rainbow stacking arc in natural, non-toxic dyed colours, a Montessori open-ended play classic.",
            "montessori-toys", DevelopmentSeedCredentials.OwnerNarekEmail,
            3500m, "AMD", "Armenia", "Yerevan", "38 Acharyan St",
            40.2223m, 44.5751m,
            ListingStatus.Approved, CreatedDaysAgo: 10, UpdatedDaysAgo: 9,
            AgeFromMonths: 12, AgeToMonths: 60,
            Condition: "Like new",
            HygieneNotes: "Wiped with a barely damp cloth only, per the manufacturer's care instructions to protect the finish.",
            SafetyNotes: "Smooth, splinter-free edges. Non-toxic water-based dyes, safe for mouthing by younger children.",
            DepositAmount: 14000m),
        new(
            ListingIds.MelissaDougWoodenPegPuzzleFarmAnimals,
            "Melissa & Doug Wooden Peg Puzzle - Farm Animals",
            "Chunky wooden peg puzzle with eight farm-animal pieces, each with an easy-grip knob for small hands.",
            "puzzles", DevelopmentSeedCredentials.OwnerLilitEmail,
            1300m, "AMD", "Armenia", "Yerevan", "Davtashen 3rd District, Bldg 27",
            40.2273m, 44.4821m,
            ListingStatus.Approved, CreatedDaysAgo: 17, UpdatedDaysAgo: 1,
            AgeFromMonths: 18, AgeToMonths: 48,
            Condition: "Excellent",
            HygieneNotes: "Pegs and board wiped with a damp cloth and mild soap, dried fully before each handover.",
            SafetyNotes: "Knobs are glued and rounded, no splinters or detachable small parts.",
            DepositAmount: 5000m),
        new(
            ListingIds.RavensburgerLabyrinthJunior,
            "Ravensburger Labyrinth Junior",
            "Junior sliding-maze board game with double-sided tiles for two difficulty levels, all 24 tiles present.",
            "board-games", DevelopmentSeedCredentials.OwnerDavitEmail,
            1800m, "AMD", "Armenia", "Yerevan", "201 Arshakunyats Ave",
            40.1520m, 44.5550m,
            ListingStatus.Approved, CreatedDaysAgo: 24, UpdatedDaysAgo: 9,
            AgeFromMonths: 48, AgeToMonths: 84,
            Condition: "Like new",
            HygieneNotes: "Board and tiles wiped with a dry cloth; box interior dividers wiped clean between rentals.",
            SafetyNotes: "No small detachable parts beyond the tiles, which are sized well above choking-hazard limits.",
            DepositAmount: 7000m),
        new(
            ListingIds.KidsKaraokeMachineWithDiscoLights,
            "Kids Karaoke Machine with Disco Lights",
            "Bluetooth karaoke machine with two wireless microphones, built-in disco lights and a tablet holder.",
            "party-toys", DevelopmentSeedCredentials.OwnerMariamEmail,
            3500m, "AMD", "Armenia", "Yerevan", "67 Zoravar Andranik Ave",
            40.2148m, 44.5435m,
            ListingStatus.Approved, CreatedDaysAgo: 31, UpdatedDaysAgo: 3,
            AgeFromMonths: 60, AgeToMonths: 144,
            Condition: "Excellent",
            HygieneNotes: "Microphone foam covers replaced between rentals; casing wiped with an electronics-safe disinfectant wipe.",
            SafetyNotes: "Volume is capped at a hearing-safe maximum. Runs on a low-voltage adapter, no exposed wiring.",
            DepositAmount: 14000m),
        new(
            ListingIds.VTechSitToStandLearningWalker,
            "VTech Sit-to-Stand Learning Walker",
            "Push-along learning walker with a removable activity panel (lights, sounds, shape sorter) and adjustable speed wheels for early walkers.",
            "baby-toys", DevelopmentSeedCredentials.DemoOwnerEmail,
            1800m, "AMD", "Armenia", "Yerevan", "12 Nalbandyan St",
            40.1823m, 44.5157m,
            ListingStatus.Approved, CreatedDaysAgo: 38, UpdatedDaysAgo: 21,
            AgeFromMonths: 9, AgeToMonths: 18,
            Condition: "Good",
            HygieneNotes: "Wheels and handle wiped with antibacterial spray; activity panel buttons cleaned individually between rentals.",
            SafetyNotes: "Wheel resistance is adjustable to prevent the walker from running away from a new walker. No small detachable pieces.",
            DepositAmount: 7000m),
        new(
            ListingIds.MagnaTilesClearColors32PieceSet,
            "Magna-Tiles Clear Colors 32-Piece Set",
            "32-piece magnetic building tile set in translucent colours, works on any flat surface or window.",
            "building-blocks", DevelopmentSeedCredentials.OwnerAnahitEmail,
            3800m, "AMD", "Armenia", "Yerevan", "72 Sebastia St",
            40.1778m, 44.4521m,
            ListingStatus.Approved, CreatedDaysAgo: 45, UpdatedDaysAgo: 39,
            AgeFromMonths: 36, AgeToMonths: 96,
            Condition: "Like new",
            HygieneNotes: "Tiles wiped individually with a soft cloth and alcohol-free sanitiser; magnets checked for cracks.",
            SafetyNotes: "Magnets are fully sealed inside the plastic tiles and tested for secure casing before every rental.",
            DepositAmount: 18000m),
        new(
            ListingIds.MelissaDougWoodenAlphabetPuzzleBoard,
            "Melissa & Doug Wooden Alphabet Puzzle Board",
            "Solid wood puzzle board with 26 chunky letter pieces, each with a matching picture peg.",
            "educational-toys", DevelopmentSeedCredentials.OwnerNarekEmail,
            1500m, "AMD", "Armenia", "Yerevan", "Nork 3rd Microdistrict, Bldg 21",
            40.1844m, 44.5427m,
            ListingStatus.Rejected, CreatedDaysAgo: 9, UpdatedDaysAgo: 8,
            AgeFromMonths: 24, AgeToMonths: 60,
            Condition: "Like new",
            HygieneNotes: "Wooden pieces wiped with a barely damp cloth and air-dried fully before storage.",
            SafetyNotes: "Peg knobs are glued and sanded smooth; no loose or detachable small parts.",
            DepositAmount: 6000m,
            RejectionReason: "The listed condition ('Like new') doesn't match the visible wear in the submitted photos -- please update the condition or provide clearer photos before resubmitting."),
        new(
            ListingIds.Step2NaturallyPlayfulSandTable,
            "Step2 Naturally Playful Sand Table",
            "Two-level sand and water table with a sifting funnel, two buckets and four sand tools included.",
            "outdoor-toys", DevelopmentSeedCredentials.OwnerLilitEmail,
            3200m, "AMD", "Armenia", "Yerevan", "Nor Nork 2nd Microdistrict, Bldg 29",
            40.1875m, 44.5792m,
            ListingStatus.Approved, CreatedDaysAgo: 59, UpdatedDaysAgo: 16,
            AgeFromMonths: 18, AgeToMonths: 60,
            Condition: "Good",
            HygieneNotes: "Sand replaced between rentals; table basin scrubbed and rinsed, tools washed in soapy water.",
            SafetyNotes: "Rounded edges throughout; sand is non-toxic play sand certified for children's use.",
            DepositAmount: 12000m),
        new(
            ListingIds.PegPeregoJohnDeereGroundForceRideOnTractor,
            "Peg Perego John Deere Ground Force Ride-On Tractor",
            "Battery-powered ride-on tractor with a working trailer, real engine sounds and two-speed forward plus reverse.",
            "ride-on-toys", DevelopmentSeedCredentials.OwnerDavitEmail,
            7500m, "AMD", "Armenia", "Yerevan", "Nubarashen 1st Microdistrict, Bldg 14",
            40.0878m, 44.5284m,
            ListingStatus.Approved, CreatedDaysAgo: 6, UpdatedDaysAgo: 3,
            AgeFromMonths: 36, AgeToMonths: 84,
            Condition: "Good",
            HygieneNotes: "Seat and steering wheel wiped with disinfectant; battery contacts checked and cleaned of corrosion.",
            SafetyNotes: "Speed-limited to a safe walking pace. Battery charger included with clear charging instructions.",
            DepositAmount: 30000m),
        new(
            ListingIds.FisherPriceLittlePeopleFarm,
            "Fisher-Price Little People Farm",
            "Animal-sound farm playset with a barn, silo, tractor and six Little People figures.",
            "pretend-play", DevelopmentSeedCredentials.OwnerMariamEmail,
            2200m, "AMD", "Armenia", "Yerevan", "112 Bagratunyats Ave",
            40.1258m, 44.4821m,
            ListingStatus.Approved, CreatedDaysAgo: 13, UpdatedDaysAgo: 9,
            AgeFromMonths: 12, AgeToMonths: 48,
            Condition: "Good",
            HygieneNotes: "Figures and barn surfaces wiped with baby-safe disinfectant; sound module checked for sticky buttons.",
            SafetyNotes: "Figures are oversized to avoid choking hazards. Battery compartment is screw-secured.",
            DepositAmount: 9000m),
        new(
            ListingIds.PlanToysWoodenSortingBoard,
            "Plan Toys Wooden Sorting Board",
            "Sustainably made wooden sorting board with geometric pegs in four shapes and four colours.",
            "montessori-toys", DevelopmentSeedCredentials.DemoOwnerEmail,
            1800m, "AMD", "Armenia", "Yerevan", "9 Gubkin St",
            40.1998m, 44.4427m,
            ListingStatus.Approved, CreatedDaysAgo: 20, UpdatedDaysAgo: 9,
            AgeFromMonths: 18, AgeToMonths: 48,
            Condition: "Good",
            HygieneNotes: "Pegs and board wiped with a damp cloth; no submerging in water to protect the wood finish.",
            SafetyNotes: "Pegs are large-format and rounded. Board has no sharp corners.",
            DepositAmount: 7000m),
        new(
            ListingIds.Educa300PieceKidsPuzzleDinosaurs,
            "Educa 300-Piece Kids Puzzle - Dinosaurs",
            "300-piece dinosaur-themed jigsaw with an anti-slip finish, includes the original storage tin.",
            "puzzles", DevelopmentSeedCredentials.OwnerAnahitEmail,
            1500m, "AMD", "Armenia", "Yerevan", "7 Vratsakan St",
            40.2107m, 44.5263m,
            ListingStatus.Approved, CreatedDaysAgo: 27, UpdatedDaysAgo: 21,
            AgeFromMonths: 72, AgeToMonths: 144,
            Condition: "Good",
            HygieneNotes: "Pieces wiped with a dry microfibre cloth; tin interior wiped clean between rentals.",
            SafetyNotes: "Recommended for ages 6 and up given the piece count and puzzle complexity.",
            DepositAmount: 6000m),
        new(
            ListingIds.CatanJunior,
            "Catan Junior",
            "Pirate-themed junior version of Catan for 2-4 players, includes all ships, resource cards and building pieces.",
            "board-games", DevelopmentSeedCredentials.OwnerNarekEmail,
            2200m, "AMD", "Armenia", "Yerevan", "11 Davit Anhaght St",
            40.2153m, 44.5651m,
            ListingStatus.Approved, CreatedDaysAgo: 34, UpdatedDaysAgo: 19,
            AgeFromMonths: 72, AgeToMonths: 144,
            Condition: "Good",
            HygieneNotes: "Cards wiped with a dry cloth; plastic ship and building pieces washed in soapy water and dried.",
            SafetyNotes: "Small resource tokens are present -- recommended for ages 6 and up with adult supervision if younger siblings are nearby.",
            DepositAmount: 9000m),
        new(
            ListingIds.NerfRivalPartyBlasterSetX4,
            "Nerf Rival Party Blaster Set (x4)",
            "Set of four Nerf Rival blasters with foam rounds and eye-protection glasses for each player, ideal for backyard party games.",
            "party-toys", DevelopmentSeedCredentials.OwnerLilitEmail,
            2500m, "AMD", "Armenia", "Yerevan", "Davtashen 4th District, Bldg 9",
            40.2193m, 44.4751m,
            ListingStatus.PendingApproval, CreatedDaysAgo: 2, UpdatedDaysAgo: 1,
            AgeFromMonths: 96, AgeToMonths: 168,
            Condition: "Good",
            HygieneNotes: "Blasters wiped down with antibacterial wipes; foam rounds inspected and replaced if worn.",
            SafetyNotes: "Eye-protection glasses are included and required for all players. Foam rounds only, no hard projectiles.",
            DepositAmount: 10000m),
        new(
            ListingIds.MunchkinBathToyOrganizerSquirtersSet,
            "Munchkin Bath Toy Organizer & Squirters Set",
            "Mesh bath toy organizer with a suction-mount hook, plus eight squirter toys (boats, ducks and sea animals).",
            "baby-toys", DevelopmentSeedCredentials.OwnerDavitEmail,
            1200m, "AMD", "Armenia", "Yerevan", "15 Nor Aresh St",
            40.1450m, 44.5430m,
            ListingStatus.Approved, CreatedDaysAgo: 48, UpdatedDaysAgo: 21,
            AgeFromMonths: 6, AgeToMonths: 36,
            Condition: "Used",
            HygieneNotes: "Squirters boiled for two minutes and left to fully dry before each rental to prevent internal mould.",
            SafetyNotes: "No small removable parts. Suction hook tested on tile before handover; mesh bag is machine-washable.",
            DepositAmount: 5000m),
        new(
            ListingIds.LEGOTechnicOffRoadBuggy,
            "LEGO Technic Off-Road Buggy",
            "Advanced LEGO Technic buggy with working suspension and steering, 374 pieces. Built and rebuilt twice to confirm no pieces are missing.",
            "building-blocks", DevelopmentSeedCredentials.OwnerMariamEmail,
            3500m, "AMD", "Armenia", "Yerevan", "13 Kanaker St",
            40.2078m, 44.5335m,
            ListingStatus.Approved, CreatedDaysAgo: 55, UpdatedDaysAgo: 39,
            AgeFromMonths: 84, AgeToMonths: 168,
            Condition: "Good",
            HygieneNotes: "Pieces wiped down with a dry microfibre cloth; axle and gear mechanisms checked and cleaned of dust.",
            SafetyNotes: "Small technical pieces are recommended for children 7+ with supervision. No sharp edges on moving parts.",
            DepositAmount: 16000m),
        new(
            ListingIds.OsmoGeniusStarterKitForIPad,
            "Osmo Genius Starter Kit for iPad",
            "Osmo Genius Starter Kit (base, reflective mirror and five game sets) for interactive tablet-based learning. Tablet not included.",
            "educational-toys", DevelopmentSeedCredentials.DemoOwnerEmail,
            3500m, "AMD", "Armenia", "Yerevan", "63 Abovyan St",
            40.1773m, 44.5067m,
            ListingStatus.Approved, CreatedDaysAgo: 2, UpdatedDaysAgo: 1,
            AgeFromMonths: 36, AgeToMonths: 96,
            Condition: "Excellent",
            HygieneNotes: "Base, mirror and game pieces wiped with screen-safe disinfectant wipes between rentals.",
            SafetyNotes: "Compatible with most iPad models but requires the renter's own tablet; base clips are tension-tested.",
            DepositAmount: 15000m),
        new(
            ListingIds.HedstromRainbowWaterSprinklerPlayMat,
            "Hedstrom Rainbow Water Sprinkler Play Mat",
            "Inflatable splash pad that connects to a garden hose, with colourful sprinkler jets around the rim.",
            "outdoor-toys", DevelopmentSeedCredentials.OwnerAnahitEmail,
            1800m, "AMD", "Armenia", "Yerevan", "8 Raffi St",
            40.1718m, 44.4431m,
            ListingStatus.Approved, CreatedDaysAgo: 9, UpdatedDaysAgo: 3,
            AgeFromMonths: 12, AgeToMonths: 72,
            Condition: "Used",
            HygieneNotes: "Rinsed inside and out with a hose after each use and left to air-dry fully to prevent mildew.",
            SafetyNotes: "Low-profile design with no deep water pooling. Adult supervision recommended near the hose connection.",
            DepositAmount: 6000m),
        new(
            ListingIds.Strider12SportBalanceBike,
            "Strider 12 Sport Balance Bike",
            "Ultra-light 12-inch balance bike with a footrest and adjustable seat/handlebar height for growing riders.",
            "ride-on-toys", DevelopmentSeedCredentials.OwnerNarekEmail,
            2800m, "AMD", "Armenia", "Yerevan", "23 Marash St",
            40.1784m, 44.5327m,
            ListingStatus.Approved, CreatedDaysAgo: 16, UpdatedDaysAgo: 9,
            AgeFromMonths: 18, AgeToMonths: 60,
            Condition: "Like new",
            HygieneNotes: "Frame and grips wiped with disinfectant spray; tyres checked and wiped free of outdoor debris.",
            SafetyNotes: "No pedals or chain to catch fingers or clothing. Owner recommends a properly fitted helmet.",
            DepositAmount: 11000m),
        new(
            ListingIds.PlaymobilGrandCastlePlayset,
            "Playmobil Grand Castle Playset",
            "Detailed medieval castle playset with a working drawbridge, catapult and eight knight and royal figures.",
            "pretend-play", DevelopmentSeedCredentials.OwnerLilitEmail,
            4000m, "AMD", "Armenia", "Yerevan", "Nor Nork 5th Microdistrict, Bldg 11",
            40.1805m, 44.5692m,
            ListingStatus.Approved, CreatedDaysAgo: 23, UpdatedDaysAgo: 23,
            AgeFromMonths: 48, AgeToMonths: 108,
            Condition: "Like new",
            HygieneNotes: "Figures and accessories washed in a mesh laundry bag; castle towers wiped down individually.",
            SafetyNotes: "Catapult launches small foam projectiles only, no hard parts. Not recommended under 4 years due to small figures.",
            DepositAmount: 16000m),
        new(
            ListingIds.LoveveryPlayKitTheBabbler,
            "Lovevery Play Kit - The Babbler",
            "Stage-based Montessori play kit for 6-9 months: textured balls, a rolling drum and a first mirror toy.",
            "montessori-toys", DevelopmentSeedCredentials.OwnerDavitEmail,
            2600m, "AMD", "Armenia", "Yerevan", "Nubarashen 2nd Microdistrict, Bldg 9",
            40.0848m, 44.5234m,
            ListingStatus.PendingApproval, CreatedDaysAgo: 5, UpdatedDaysAgo: 3,
            AgeFromMonths: 6, AgeToMonths: 9,
            Condition: "Excellent",
            HygieneNotes: "All pieces are dishwasher-safe on the top rack and are run through a cycle between every rental.",
            SafetyNotes: "Designed and lab-tested for mouthing at this age. No small detachable parts.",
            DepositAmount: 10000m),
        new(
            ListingIds.JanodMagneticWoodenPuzzleBookSeasons,
            "Janod Magnetic Wooden Puzzle Book - Seasons",
            "Magnetic travel puzzle book with four seasonal scenes and 30+ magnetic wooden pieces stored inside the cover.",
            "puzzles", DevelopmentSeedCredentials.OwnerMariamEmail,
            1600m, "AMD", "Armenia", "Yerevan", "29 Manandyan St",
            40.1188m, 44.4721m,
            ListingStatus.Approved, CreatedDaysAgo: 37, UpdatedDaysAgo: 4,
            AgeFromMonths: 36, AgeToMonths: 84,
            Condition: "Like new",
            HygieneNotes: "Magnetic pieces wiped individually with a damp cloth; book cover wiped and dried before storage.",
            SafetyNotes: "Magnets are embedded in wooden pieces and glue-sealed; checked for any loose magnets before each rental.",
            DepositAmount: 6000m),
        new(
            ListingIds.JengaClassicWoodenBlockGame,
            "Jenga Classic Wooden Block Game",
            "Original 54-piece wooden stacking tower game, blocks sanded smooth and warp-checked.",
            "board-games", DevelopmentSeedCredentials.DemoOwnerEmail,
            1400m, "AMD", "Armenia", "Yerevan", "64 Komitas Ave",
            40.2187m, 44.5283m,
            ListingStatus.Approved, CreatedDaysAgo: 44, UpdatedDaysAgo: 29,
            AgeFromMonths: 72, AgeToMonths: 168,
            Condition: "Used",
            HygieneNotes: "Wooden blocks wiped with a barely damp cloth; fully dried before restacking to prevent warping.",
            SafetyNotes: "Blocks are solid wood with no splinters. A falling tower poses no injury risk beyond minor bumps.",
            DepositAmount: 5500m),
        new(
            ListingIds.PinataPartyFavorBundle,
            "Pinata & Party Favor Bundle",
            "Reusable star-shaped pinata frame with a party favor refill kit (confetti, small toys and wrapped candy bags).",
            "party-toys", DevelopmentSeedCredentials.OwnerAnahitEmail,
            1800m, "AMD", "Armenia", "Yerevan", "38 Tumanyan St",
            40.1843m, 44.5127m,
            ListingStatus.Approved, CreatedDaysAgo: 51, UpdatedDaysAgo: 3,
            AgeFromMonths: 36, AgeToMonths: 144,
            Condition: "Used",
            HygieneNotes: "Pinata shell wiped clean after each use; favor bags are single-use and replaced fresh for every rental.",
            SafetyNotes: "Pinata stick is foam-padded. Adult supervision required during the pinata activity.",
            DepositAmount: 6000m)
    ];

    // Seed images: primary source is Unsplash (downloaded and stored locally at startup).
    // FallbackUrl is used when the download fails (no internet), keeping the app functional.
    public static readonly SeedListingImage[] ListingImages =
    [
        new(new Guid("33333333-0001-4000-9000-000000000001"), ListingIds.LegoDuploStarterSet,
            "https://images.unsplash.com/photo-1587654780291-39c9404d746b?w=800&q=80",
            IsPrimary: true, SortOrder: 0, FallbackUrl: "/assets/categories/building-blocks.svg"),
        new(new Guid("33333333-0001-4000-9000-000000000002"), ListingIds.LegoDuploStarterSet,
            "https://images.unsplash.com/photo-1558060370-d644479cb6f7?w=800&q=80",
            IsPrimary: false, SortOrder: 1, FallbackUrl: "/assets/categories/building-blocks.svg"),
        new(new Guid("33333333-0002-4000-9000-000000000001"), ListingIds.MontessoriWoodenToySet,
            "https://images.unsplash.com/photo-1581235720704-06d3acfcb36f?w=800&q=80",
            IsPrimary: true, SortOrder: 0, FallbackUrl: "/assets/categories/montessori-toys.svg"),
        new(new Guid("33333333-0002-4000-9000-000000000002"), ListingIds.MontessoriWoodenToySet,
            "https://images.unsplash.com/photo-1581235720704-06d3acfcb36f?w=800&q=80",
            IsPrimary: false, SortOrder: 1, FallbackUrl: "/assets/categories/montessori-toys.svg"),
        new(new Guid("33333333-0003-4000-9000-000000000001"), ListingIds.BabyActivityGym,
            "https://images.unsplash.com/photo-1515488764276-beab7607c1e6?w=800&q=80",
            IsPrimary: true, SortOrder: 0, FallbackUrl: "/assets/categories/baby-toys.svg"),
        new(new Guid("33333333-0004-4000-9000-000000000001"), ListingIds.KidsBalanceBike,
            "https://images.unsplash.com/photo-1558618666-fcd25c85cd64?w=800&q=80",
            IsPrimary: true, SortOrder: 0, FallbackUrl: "/assets/categories/ride-on-toys.svg"),
        new(new Guid("33333333-0004-4000-9000-000000000002"), ListingIds.KidsBalanceBike,
            "https://images.unsplash.com/photo-1596462502278-27bfdc403348?w=800&q=80",
            IsPrimary: false, SortOrder: 1, FallbackUrl: "/assets/categories/ride-on-toys.svg"),
        new(new Guid("33333333-0005-4000-9000-000000000001"), ListingIds.OutdoorBackyardSlide,
            "https://images.unsplash.com/photo-1575783970733-1aaedde1db74?w=800&q=80",
            IsPrimary: true, SortOrder: 0, FallbackUrl: "/assets/categories/outdoor-toys.svg"),
        new(new Guid("33333333-0006-4000-9000-000000000001"), ListingIds.ChildrensPuzzleBundle,
            "https://images.unsplash.com/photo-1577563908411-5077b6dc7624?w=800&q=80",
            IsPrimary: true, SortOrder: 0, FallbackUrl: "/assets/categories/puzzles.svg"),
        new(new Guid("33333333-0007-4000-9000-000000000001"), ListingIds.ToyKitchenSet,
            "https://images.unsplash.com/photo-1566576912321-d58ddd7a6088?w=800&q=80",
            IsPrimary: true, SortOrder: 0, FallbackUrl: "/assets/categories/pretend-play.svg"),
        new(new Guid("33333333-0007-4000-9000-000000000002"), ListingIds.ToyKitchenSet,
            "https://images.unsplash.com/photo-1604671801908-6f0c6a092c05?w=800&q=80",
            IsPrimary: false, SortOrder: 1, FallbackUrl: "/assets/categories/pretend-play.svg"),
        new(new Guid("33333333-0008-4000-9000-000000000001"), ListingIds.BoardGameFamilyBundle,
            "https://images.unsplash.com/photo-1611891487122-207579d67d98?w=800&q=80",
            IsPrimary: true, SortOrder: 0, FallbackUrl: "/assets/categories/board-games.svg"),
        new(new Guid("33333333-0009-4000-9000-000000000001"), ListingIds.BirthdayPartyToyPack,
            "https://images.unsplash.com/photo-1530103862676-de8c9debad1d?w=800&q=80",
            IsPrimary: true, SortOrder: 0, FallbackUrl: "/assets/categories/party-toys.svg"),
        new(new Guid("33333333-000a-4000-9000-00000000000a"), ListingIds.SoftPlayFoamSet,
            "https://images.unsplash.com/photo-1596461404969-9ae70f2830c1?w=800&q=80",
            IsPrimary: true, SortOrder: 0, FallbackUrl: "/assets/categories/educational-toys.svg"),
        new(new Guid("33333333-000b-4000-9000-00000000000b"), ListingIds.StemScienceDiscoveryKit,
            "https://images.unsplash.com/photo-1532619675605-1ede6c2ed2b0?w=800&q=80",
            IsPrimary: true, SortOrder: 0, FallbackUrl: "/assets/categories/educational-toys.svg"),
        new(new Guid("33333333-000b-4000-9000-00000000000c"), ListingIds.StemScienceDiscoveryKit,
            "https://images.unsplash.com/photo-1564156280315-1d42b4651629?w=800&q=80",
            IsPrimary: false, SortOrder: 1, FallbackUrl: "/assets/categories/educational-toys.svg"),
        new(new Guid("33333333-000c-4000-9000-00000000000b"), ListingIds.ClassicBoardGameTrio,
            "https://images.unsplash.com/photo-1606503825008-909a67e63c3d?w=800&q=80",
            IsPrimary: true, SortOrder: 0, FallbackUrl: "/assets/categories/board-games.svg"),
        new(new Guid("33333333-000d-4000-9000-00000000000b"), ListingIds.PartyFunActivityPack,
            "https://images.unsplash.com/photo-1492684223066-81342ee5ff30?w=800&q=80",
            IsPrimary: true, SortOrder: 0, FallbackUrl: "/assets/categories/party-toys.svg"),
        new(new Guid("33333333-000d-4000-9000-00000000000c"), ListingIds.PartyFunActivityPack,
            "https://images.unsplash.com/photo-1530103862676-de8c9debad1d?w=800&q=80",
            IsPrimary: false, SortOrder: 1, FallbackUrl: "/assets/categories/party-toys.svg"),
        new(new Guid("33333333-000e-4000-9000-00000000000b"), ListingIds.WoodenTrainSet,
            "https://images.unsplash.com/photo-1594736797933-d0501ba2fe65?w=800&q=80",
            IsPrimary: true, SortOrder: 0, FallbackUrl: "/assets/categories/building-blocks.svg"),
        new(new Guid("33333333-000e-4000-9000-00000000000c"), ListingIds.WoodenTrainSet,
            "https://images.unsplash.com/photo-1558060370-d644479cb6f7?w=800&q=80",
            IsPrimary: false, SortOrder: 1, FallbackUrl: "/assets/categories/building-blocks.svg"),
        new(new Guid("33333333-000f-4000-9000-00000000000b"), ListingIds.KidsArtEasel,
            "https://images.unsplash.com/photo-1513364776144-60967b0f800f?w=800&q=80",
            IsPrimary: true, SortOrder: 0, FallbackUrl: "/assets/categories/pretend-play.svg")
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

        // ---- Lifecycle demo bookings (owner@ listings, rented by renter@) ----
        // Active: owner handed the toy over, rental in progress (started, not yet ended).
        new(new Guid("55555555-000b-4000-9000-00000000000b"), ListingIds.BabyActivityGym,
            DevelopmentSeedCredentials.RenterEmail,
            StartDaysFromToday: -2, DurationDays: 7, BookingStatus.Active,
            ExpiresAtHoursFromNow: -48, CreatedDaysAgo: 5),
        // Active but past its end date — the owner has not yet confirmed completion.
        new(new Guid("55555555-000c-4000-9000-00000000000c"), ListingIds.KidsBalanceBike,
            DevelopmentSeedCredentials.RenterEmail,
            StartDaysFromToday: -5, DurationDays: 4, BookingStatus.Active,
            ExpiresAtHoursFromNow: -120, CreatedDaysAgo: 7),
        // Completed — owner confirmed the toy was returned.
        new(new Guid("55555555-000d-4000-9000-00000000000d"), ListingIds.ChildrensPuzzleBundle,
            DevelopmentSeedCredentials.RenterEmail,
            StartDaysFromToday: -6, DurationDays: 4, BookingStatus.Completed,
            ExpiresAtHoursFromNow: -144, CreatedDaysAgo: 8)
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

    // Admin console Phase 4: fixed report GUIDs. Seeded directly (bespoke, hand-written) rather
    // than through a declarative SeedReport[] table — same convention as SeedChatAsync — since
    // each row's target is a different polymorphic kind (listing / user / conversation) and the
    // set is small. See DevelopmentSeedRunner.SeedReportsAsync.
    public static class ReportIds
    {
        public static readonly Guid UnsafeBalanceBike = new("99999999-0001-4000-9000-000000000001");
        public static readonly Guid MisleadingKitchenPhotos = new("99999999-0001-4000-9000-000000000002");
        public static readonly Guid RenterNoShow = new("99999999-0001-4000-9000-000000000003");
        public static readonly Guid RudeMessagesResolved = new("99999999-0001-4000-9000-000000000004");
        public static readonly Guid OffPlatformPaymentDismissed = new("99999999-0001-4000-9000-000000000005");
        public static readonly Guid SpamListing = new("99999999-0001-4000-9000-000000000006");
        public static readonly Guid OtherOwnerFlag = new("99999999-0001-4000-9000-000000000007");
    }
}
