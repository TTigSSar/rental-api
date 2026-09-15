namespace RentalPlatform.Domain.Enums;

// How a renter may receive the toy — additive, multi-select successor to DeliveryType (see that
// file). An owner may now offer Pickup, Courier, or both, so this is a [Flags] enum rather than a
// scalar. DeliveryType stays on the entity as the legacy single value, mirrored from this one for
// backward compatibility.
[Flags]
public enum DeliveryOptions
{
    None = 0,
    Pickup = 1,
    Courier = 2
}
