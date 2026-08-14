namespace RentalPlatform.Domain.Enums;

// Admin console Phase 3: a user's account status is DERIVED (from IsBlocked/IsIdConfirmed), never
// persisted — no new column, no migration. Suspended takes priority over Pending: a blocked user
// is Suspended regardless of ID-confirmation state.
public enum UserAccountStatus
{
    Pending = 0,
    Active = 1,
    Suspended = 2
}
