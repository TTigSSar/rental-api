namespace RentalPlatform.Domain.Enums;

/// <summary>Which side of a booking performed an action (e.g. who initiated the return handshake).</summary>
public enum BookingParty
{
    Renter = 0,
    Owner = 1
}
