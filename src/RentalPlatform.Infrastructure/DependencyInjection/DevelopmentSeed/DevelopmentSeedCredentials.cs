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
///   demo_owner@toyrent.am — Owner with 12 approved listings across all categories
///   demo_renter@toyrent.am— Renter with bookings and favorites
/// </remarks>
internal static class DevelopmentSeedCredentials
{
    public const string Password = "LocalDemo123!";

    public const string AdminEmail = "admin@rental.local";
    public const string OwnerEmail = "owner@rental.local";
    public const string RenterEmail = "renter@rental.local";
    public const string SecondUserEmail = "user2@rental.local";
    public const string BlockedEmail = "blocked@rental.local";

    // Docker / public demo accounts
    public const string DemoAdminEmail  = "admin@toyrent.am";
    public const string DemoOwnerEmail  = "demo_owner@toyrent.am";
    public const string DemoRenterEmail = "demo_renter@toyrent.am";
}
