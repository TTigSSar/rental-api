namespace RentalPlatform.Infrastructure.DependencyInjection.DevelopmentSeed;

/// <summary>
/// Single source of truth for local-development demo credentials.
/// Used by the development seeder only and never in production code paths.
/// </summary>
/// <remarks>
/// Demo login (password is the same for every demo account):
///   admin@rental.local    — Admin, moderates pending listings
///   owner@rental.local    — User, owns several listings across categories
///   renter@rental.local   — User, books listings and uses favorites
///   user2@rental.local    — User, secondary account for favorite/ownership checks
///   blocked@rental.local  — User, IsBlocked = true, for auth rejection testing
///
/// Docker/demo accounts (toyrent.am domain):
///   admin@toyrent.am      — Admin demo account
///   demo_owner@toyrent.am — Owner with 14 approved listings across all categories
///   demo_renter@toyrent.am— Renter with bookings and favorites
///
/// Additional catalogue-expansion owners (toyrent.am domain, no bookings/reviews/favorites):
///   anahit@toyrent.am, narek@toyrent.am, lilit@toyrent.am, davit@toyrent.am, mariam@toyrent.am
///
/// Home-point cohort (toyrent.am domain, no bookings/reviews/favorites):
///   gohar@toyrent.am, karen@toyrent.am, armen@toyrent.am, seda@toyrent.am, vahe@toyrent.am,
///   hasmik@toyrent.am
/// A listing's location is now its owner's home point, so a catalogue that is spread across the map
/// needs OWNERS spread across the map. These six bring the seed to 13 owners with a home point,
/// covering all 12 Yerevan districts (hasmik@toyrent.am shares Nubarashen with seda@toyrent.am, on
/// a different street). All 13 are inside Yerevan because nothing else can be saved as a home point.
/// </remarks>
internal static class DevelopmentSeedCredentials
{
    // Password used for ALL seeded accounts (local dev + Docker demo).
    public const string Password = "Demo1234";

    public const string AdminEmail = "admin@rental.local";
    public const string OwnerEmail = "owner@rental.local";
    public const string RenterEmail = "renter@rental.local";
    public const string SecondUserEmail = "user2@rental.local";
    public const string BlockedEmail = "blocked@rental.local";

    // Docker / public demo accounts
    public const string DemoAdminEmail  = "admin@toyrent.am";
    public const string DemoOwnerEmail  = "demo_owner@toyrent.am";
    public const string DemoRenterEmail = "demo_renter@toyrent.am";

    // Additional owners backing the 50-listing toy-catalogue expansion (2026-08).
    public const string OwnerAnahitEmail = "anahit@toyrent.am";
    public const string OwnerNarekEmail  = "narek@toyrent.am";
    public const string OwnerLilitEmail  = "lilit@toyrent.am";
    public const string OwnerDavitEmail  = "davit@toyrent.am";
    public const string OwnerMariamEmail = "mariam@toyrent.am";

    // Home-point cohort (2026-09): one owner per remaining Yerevan district, plus a 13th sharing
    // Nubarashen — see DevelopmentSeedData.HomePoints for why that district doubles up.
    // See DevelopmentSeedData.OwnerHomePoints for the district each of these lives in.
    public const string OwnerGoharEmail  = "gohar@toyrent.am";
    public const string OwnerKarenEmail  = "karen@toyrent.am";
    public const string OwnerArmenEmail  = "armen@toyrent.am";
    public const string OwnerSedaEmail   = "seda@toyrent.am";
    public const string OwnerVaheEmail   = "vahe@toyrent.am";
    public const string OwnerHasmikEmail = "hasmik@toyrent.am";
}
