namespace RentalPlatform.Application.Common;

// The place names the platform assigns itself now that a listing's location is no longer typed by
// the owner but derived from their home point (home-point model). DoRent is an Armenia-only
// marketplace with an Armenia-only home-point bounding box (see HomePointValidation), so the
// country is a constant rather than a field anyone can set.
public static class LocationDefaults
{
    public const string Country = "Armenia";

    // The city of every listing the platform accepts today. A home point can only be saved inside
    // one of the 12 Yerevan districts (IHomePointService.ValidateForSave), and a listing's location
    // is its owner's home point, so this is not a default so much as the only answer — both at
    // create time (ListingsOwnerService.ResolveCity) and whenever a home point moves
    // (HomePointService, which rewrites City along with the coordinates).
    //
    // A previous revision also had an "unknown city" constant for a home point outside Yerevan.
    // That case can no longer be created; the only rows that still have one are legacy home points
    // the AddUserHomePoint migration derived before the rule existed, and those keep the city their
    // own listings already carry rather than being relabelled.
    public const string YerevanCity = "Yerevan";
}
