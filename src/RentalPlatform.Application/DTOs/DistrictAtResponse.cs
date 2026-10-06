namespace RentalPlatform.Application.DTOs;

// Answer to "which Yerevan district is this coordinate in?" — the live readout the map shows while
// the pin is still being dragged, before any account or listing exists. Anonymous by necessity: the
// sign-up wizard asks this question before the user has an account.
//
// Nothing here is sensitive: the caller already knows the coordinate (they chose it), and the
// district boundaries are public OSM data resolved locally (IDistrictBoundaryProvider). The
// endpoint is rate-limited per IP all the same, because it is the one unauthenticated endpoint that
// runs a point-in-polygon sweep per call.
public sealed class DistrictAtResponse
{
    // The district containing the point, or null for anywhere outside all 12 Yerevan districts.
    //
    // Null is the ONLY "you cannot save this" signal the client needs, and it is exactly the rule
    // the write side enforces (IHomePointService.ValidateForSave → auth.home_point_outside_yerevan).
    // An earlier version of this DTO also carried an `inArmenia` flag, computed from a coarse
    // bounding box, to distinguish "outside Yerevan" from "outside the country". That distinction
    // died with the Yerevan-only rule: both cases are refused identically, so the extra field could
    // only invite a client to treat one of them as acceptable. One field, one meaning.
    public ListingDistrictResponse? District { get; init; }
}
