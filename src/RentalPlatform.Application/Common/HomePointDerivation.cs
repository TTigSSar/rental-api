using RentalPlatform.Application.Abstractions;

namespace RentalPlatform.Application.Common;

// The single derivation used everywhere a user's home point is written: registration,
// HomePointService.SetHomePointAsync, the development seed and the demo bootstrap all resolve the
// public (fuzzed) pair and the district CODE through this same helper — see the home-point model docs and
// IGeohashSnapper/IDistrictBoundaryProvider for why there must never be a second place that decides
// either of those two things.
//
// Both inputs are assumed already validated (WGS84 range, Armenia bounding box) by the caller —
// same convention as IGeohashSnapper.SnapToCellCenter.
public static class HomePointDerivation
{
    public readonly record struct Result(decimal PublicLatitude, decimal PublicLongitude, string? DistrictCode);

    public static Result Derive(
        decimal latitude,
        decimal longitude,
        IGeohashSnapper geohashSnapper,
        IDistrictBoundaryProvider districtBoundaryProvider)
    {
        var (publicLatitude, publicLongitude) = geohashSnapper.SnapToCellCenter(latitude, longitude);
        var districtCode = districtBoundaryProvider.FindDistrictCode((double)latitude, (double)longitude);

        return new Result(publicLatitude, publicLongitude, districtCode);
    }
}
