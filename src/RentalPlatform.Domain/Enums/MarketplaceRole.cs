namespace RentalPlatform.Domain.Enums;

// Admin console Phase 3: the marketplace-activity role shown on the Users screen ("Owner" /
// "Renter" / "Both"), distinct from the system User.Role (Admin/User). Derived from activity —
// never persisted, no migration. Has listings => Owner, has bookings-as-renter => Renter, both =>
// Both, neither => Renter (the default for a brand-new account).
public enum MarketplaceRole
{
    Renter = 0,
    Owner = 1,
    Both = 2
}
