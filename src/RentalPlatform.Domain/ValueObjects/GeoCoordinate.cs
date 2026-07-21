namespace RentalPlatform.Domain.ValueObjects;

// Immutable WGS84 coordinate pair. Unlike the flat nullable Latitude/Longitude columns on
// Listing (where two independent nullable columns can legally be half-set — latitude present,
// longitude null), a GeoCoordinate cannot exist in a half-set state: the only way to obtain an
// instance is the constructor below, which requires both values and validates both ranges before
// either is assigned. `sealed record` gives structural (value) equality and immutability
// (get-only properties) for free.
public sealed record GeoCoordinate
{
    public decimal Latitude { get; }
    public decimal Longitude { get; }

    public GeoCoordinate(decimal latitude, decimal longitude)
    {
        if (latitude < -90m || latitude > 90m)
        {
            throw new ArgumentOutOfRangeException(nameof(latitude), latitude, "Latitude must be between -90 and 90 degrees.");
        }

        if (longitude < -180m || longitude > 180m)
        {
            throw new ArgumentOutOfRangeException(nameof(longitude), longitude, "Longitude must be between -180 and 180 degrees.");
        }

        Latitude = latitude;
        Longitude = longitude;
    }
}
