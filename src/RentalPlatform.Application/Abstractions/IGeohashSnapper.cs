namespace RentalPlatform.Application.Abstractions;

// Derives the "public" (privacy-safe) coordinate a non-owner/non-admin caller sees instead of a
// listing's exact Latitude/Longitude (see ListingsQueryService.GetApprovedListingByIdAsync). The
// public pair is the centroid of the fixed-precision geohash cell containing the exact point:
// every exact point inside the same cell snaps to the identical published pair, so repeated
// observation of a listing (by the same caller or different ones) yields no extra positional
// information beyond "which cell" — no trilateration by re-querying.
//
// Precision is a single constant owned by the implementation (see GeohashSnapper.Precision) —
// there must never be a second place that decides how coarse the public pair is. Every distance
// filter and distance-sort computation (ListingsQueryService: the radius filter's haversine
// refinement and ListingPreviewResponse.DistanceKm) consumes PublicLatitude/PublicLongitude only,
// never the exact pair — computing from the exact point would let a caller trilaterate it via a
// boolean in/out-of-radius oracle on an arbitrary circle, defeating the whole point of fuzzing.
public interface IGeohashSnapper
{
    // Both inputs are assumed already validated (WGS84 range) by the caller — CreateListingRequest/
    // UpdateListingRequest already enforce that via [Range] attributes before a service ever calls this.
    (decimal Latitude, decimal Longitude) SnapToCellCenter(decimal latitude, decimal longitude);
}
