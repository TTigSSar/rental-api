namespace RentalPlatform.Domain.Enums;

// What a listing's location represents. Only Home is used today (a listing's normal address).
// HandoverPoint is reserved for a future Phase 3 card (a separate meet-up point distinct from the
// owner's home address) — no behaviour branches on it yet.
public enum LocationKind
{
    Home = 0,
    HandoverPoint = 1
}
