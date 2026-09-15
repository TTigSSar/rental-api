namespace RentalPlatform.Domain.Enums;

// How a renter receives the toy. Legacy single-select value: the create-listing wizard now lets
// an owner pick Pickup, Courier, or both (see DeliveryOptions, a [Flags] enum, and
// Listing.DeliveryOptions). This scalar field is kept for backward compatibility and is mirrored
// from DeliveryOptions — Pickup when the flags include Pickup, else Courier — rather than being
// independently settable going forward.
public enum DeliveryType
{
    Pickup = 0,
    Courier = 1
}
