namespace RentalPlatform.Application.Abstractions;

// Local point-in-polygon lookup against the Yerevan district boundary asset — no geocoder
// in the correctness path. The 12 districts and their boundary polygons are a static,
// versioned data asset (Infrastructure/Resources/yerevan-districts.geojson); implementations
// load and cache it once.
public interface IDistrictBoundaryProvider
{
    // Returns the stable kebab-case district code (e.g. "kentron", "nor-nork") whose boundary
    // polygon contains the given WGS84 point, or null when the point falls outside every known
    // Yerevan district (including anywhere outside Yerevan entirely).
    string? FindDistrictCode(double latitude, double longitude);
}
