using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Domain.Entities;

public sealed class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string? PreferredLanguage { get; set; }
    public string? ExternalAuthProvider { get; set; }
    public string? ExternalProviderId { get; set; }
    public string? AvatarUrl { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsBlocked { get; set; }
    public UserRole Role { get; set; }
    public bool IsEmailConfirmed { get; set; }
    public bool IsPhoneConfirmed { get; set; }
    public bool IsIdConfirmed { get; set; }

    // Free-text, owner-typed display location (e.g. "Yerevan, Armenia") shown on the public profile.
    // PUBLIC — never store coordinates or anything precise enough to locate a home here. The actual
    // home point (private + fuzzed-public pair) lives in the HomeLatitude/HomeLongitude family below.
    public string? Location { get; set; }

    // ---- Home point (home-point model): the single source of a user's listings' location ----
    // Exact point the user dropped — visible to the user and admins only (mirrors
    // Listing.Latitude/Longitude, see ADR-008). decimal(9,6), same precision as the listing columns.
    public decimal? HomeLatitude { get; set; }
    public decimal? HomeLongitude { get; set; }

    // Geohash-cell-centroid snapped copy (see IGeohashSnapper) — what a renter is shown instead of
    // the exact pair. Derived at write time by HomePointService/HomePointDerivation; never computed
    // anywhere else.
    public decimal? HomePublicLatitude { get; set; }
    public decimal? HomePublicLongitude { get; set; }

    // Point-in-polygon district lookup (IDistrictBoundaryProvider) against HomeLatitude/HomeLongitude.
    // Nullable for two distinct reasons: the user may have no home point at all, and a LEGACY home
    // point may sit outside every Yerevan district — the AddUserHomePoint migration derived points
    // from existing listings before the Yerevan-only rule existed, and those rows are deliberately
    // not re-validated (M-038). No NEW home point can land here with a null district.
    public Guid? HomeDistrictId { get; set; }
    public District? HomeDistrict { get; set; }

    // Set whenever HomePointService.SetHomePointAsync actually changes the point (null-point-diff
    // no-ops leave this untouched). Surfaced on CurrentUserResponse.HomePoint only — self-view only.
    public DateTime? HomePointUpdatedAt { get; set; }

    public ICollection<Listing> Listings { get; set; } = new List<Listing>();
    public ICollection<Booking> Bookings { get; set; } = new List<Booking>();
    public ICollection<Favorite> Favorites { get; set; } = new List<Favorite>();
}
