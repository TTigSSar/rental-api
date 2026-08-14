using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.DTOs;

/// <summary>
/// One row of the admin Users screen — also the shape returned by GET /api/admin/users/{id} (the
/// profile modal) and by each mutation endpoint (verify/suspend/reactivate), always freshly
/// recomputed after a write.
/// </summary>
public sealed class AdminUserSummaryResponse
{
    public Guid Id { get; init; }
    public string Email { get; init; } = string.Empty;
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string? AvatarUrl { get; init; }

    // System role (Admin/User) — distinct from MarketplaceRole below.
    public UserRole Role { get; init; }

    // Derived: IsBlocked => Suspended; else IsIdConfirmed => Active; else Pending.
    public UserAccountStatus Status { get; init; }
    public bool IsIdConfirmed { get; init; }

    // Derived from activity: has listings => Owner, has bookings-as-renter => Renter, both =>
    // Both, neither => Renter.
    public MarketplaceRole MarketplaceRole { get; init; }

    // Approved listings this user owns.
    public int ListingCount { get; init; }
    // Completed bookings this user made as renter.
    public int RentalCount { get; init; }
    // Count of Open reports filed against this user.
    public int FlagCount { get; init; }

    public DateTime CreatedAt { get; init; }
}
