using RentalPlatform.Infrastructure.Services;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// Direct math tests for GeohashSnapper (P1-3) — the ONE place that decides how coarse a listing's
// public coordinate is. Encode is cross-checked against an independently published reference
// (Wikipedia's Geohash article worked example) rather than only self-consistency, the same
// discipline DistrictBoundaryProviderTests uses against Nominatim reverse geocoding.
public sealed class GeohashSnapperTests
{
    private static readonly GeohashSnapper Snapper = new();

    [Fact]
    public void Encode_Matches_The_Well_Known_Published_Reference_Value()
    {
        // Wikipedia's Geohash article worked example: (42.6, -5.6) at precision 5 encodes to
        // "ezs42" — an independently published value, not derived from this implementation.
        var hash = GeohashSnapper.Encode(42.6, -5.6, precision: 5);

        Assert.Equal("ezs42", hash);
    }

    [Fact]
    public void SnapToCellCenter_Two_Points_In_The_Same_Cell_Produce_Identical_Output()
    {
        // Independently verified (via GetCellBounds) to share a geohash-7 cell despite being two
        // different exact points ~2-3 metres apart — the "no trilateration" guarantee: repeated
        // observation of nearby exact points must not let a caller distinguish them. (At the
        // smaller geohash-7 cell size the two points must be much closer together than they were
        // at geohash-6 to still land in the same cell — a ~5-6m-apart pair that shared a cell at
        // precision 6 straddles a precision-7 boundary and no longer does.)
        var a = Snapper.SnapToCellCenter(40.1872m, 44.5152m);
        var b = Snapper.SnapToCellCenter(40.18722m, 44.51518m);

        Assert.Equal(a, b);
    }

    [Fact]
    public void SnapToCellCenter_Result_Lies_Inside_The_Cell_Containing_The_Original_Point()
    {
        const double latitude = 40.1776; // Republic Square, Kentron
        const double longitude = 44.5126;

        var (latMin, latMax, lonMin, lonMax) = GeohashSnapper.GetCellBounds(latitude, longitude, GeohashSnapper.Precision);
        var (snappedLatitude, snappedLongitude) = Snapper.SnapToCellCenter((decimal)latitude, (decimal)longitude);

        Assert.InRange((double)snappedLatitude, latMin, latMax);
        Assert.InRange((double)snappedLongitude, lonMin, lonMax);
    }

    [Fact]
    public void SnapToCellCenter_Points_Far_Apart_Produce_Different_Output()
    {
        // Republic Square (Kentron) vs the Nor Nork massif — several km apart, different cells.
        var kentron = Snapper.SnapToCellCenter(40.1776m, 44.5126m);
        var norNork = Snapper.SnapToCellCenter(40.1830m, 44.5700m);

        Assert.NotEqual(kentron, norNork);
    }

    // The geohash-7 cell measured at Yerevan's own latitude (~40.18N) — NOT the equatorial
    // figure (~0.153km x 0.153km — the two degree-spans are numerically identical at precision 7,
    // 360/2^18 == 180/2^17, so the equatorial cell happens to be square), which only holds where
    // cos(latitude) = 1. Longitude degrees shrink by cos(latitude) away from the equator, so the
    // east-west span here is noticeably narrower than the equatorial figure: roughly 0.117km (E-W)
    // x 0.153km (N-S). The north-south span does not depend on longitude, and barely on latitude
    // (the bisection of the fixed [-90,90] range is uniform), so it stays close to the equatorial
    // figure everywhere.
    [Fact]
    public void Cell_Size_At_Yerevans_Latitude_Is_Roughly_0_117km_By_0_153km_Not_The_Equatorial_Figure()
    {
        const double latitude = 40.1776; // Republic Square
        const double longitude = 44.5126;
        const double meanEarthRadiusKm = 6371.0088;

        var (latMin, latMax, lonMin, lonMax) = GeohashSnapper.GetCellBounds(latitude, longitude, GeohashSnapper.Precision);

        var kmPerDegree = meanEarthRadiusKm * Math.PI / 180.0;
        var heightKm = (latMax - latMin) * kmPerDegree;
        var widthKm = (lonMax - lonMin) * kmPerDegree * Math.Cos(latitude * Math.PI / 180.0);

        Assert.InRange(heightKm, 0.150, 0.156);
        Assert.InRange(widthKm, 0.114, 0.120);
    }
}
