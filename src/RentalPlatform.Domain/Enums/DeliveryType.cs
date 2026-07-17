namespace RentalPlatform.Domain.Enums;

// How a renter receives the toy. Single-select by design: the create-listing wizard renders two
// mutually exclusive radio rows ("Pickup from me" / "Courier delivery"), exactly one chosen —
// hence a scalar nullable enum rather than a collection/flags type.
public enum DeliveryType
{
    Pickup = 0,
    Courier = 1
}
