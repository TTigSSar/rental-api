namespace RentalPlatform.Domain.Enums;

// Which point a Location chat message shares: the owner's own home point (Home, read server-side
// from the owner's exact pair) or an owner-chosen point inside Yerevan (Other).
public enum MeetingPointKind
{
    Home = 0,
    Other = 1
}
