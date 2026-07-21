using RentalPlatform.Application.Abstractions;

namespace RentalPlatform.Infrastructure.Services;

// Snaps an exact WGS84 point to the centroid of its geohash cell at a fixed precision — the
// public-coordinate rule's entire implementation (see IGeohashSnapper). No geometry NuGet
// package: this is the standard Gustavo Niemeyer geohash bit-interleaving algorithm, written out
// by hand like DistrictBoundaryProvider's point-in-polygon test.
//
// How it works: a geohash recursively bisects the [-90, 90] (latitude) x [-180, 180] (longitude)
// box. Each bit picks a half — longitude bit first, then latitude, alternating — narrowing a
// bounding box around the point. Grouping every 5 bits into a base32 digit produces the familiar
// geohash string (Encode, below); the SAME bit sequence, read as a bounding box instead of
// characters, is exactly the "cell" the public coordinate rounds to (GetCellBounds). The public
// point is that cell's centroid, so any two exact points sharing a cell publish an identical pair.
//
// Precision 6 (30 bits: 15 longitude + 15 latitude, since 6*5=30 splits evenly) gives a cell of
// 360/2^15 degrees of longitude by 180/2^15 degrees of latitude — about 1.22 km x 0.61 km at the
// equator. Longitude degrees shrink toward the poles (by cos(latitude)), so the cell is narrower
// east-west away from the equator; see GeohashSnapperTests for the measured size at Yerevan's
// latitude (~40.18N), which is the number that actually matters here, not the equatorial textbook
// figure.
public sealed class GeohashSnapper : IGeohashSnapper
{
    // The ONE place that controls how coarse the public coordinate is. Changing this changes the
    // privacy/precision trade-off for every listing at once — do not duplicate this number
    // anywhere else (see the IGeohashSnapper doc comment).
    public const int Precision = 6;

    private const string Base32Alphabet = "0123456789bcdefghjkmnpqrstuvwxyz";
    private const int BitsPerChar = 5;

    public (decimal Latitude, decimal Longitude) SnapToCellCenter(decimal latitude, decimal longitude)
    {
        var (latMin, latMax, lonMin, lonMax) = GetCellBounds((double)latitude, (double)longitude, Precision);

        var centerLatitude = (decimal)Math.Round((latMin + latMax) / 2.0, 6, MidpointRounding.AwayFromZero);
        var centerLongitude = (decimal)Math.Round((lonMin + lonMax) / 2.0, 6, MidpointRounding.AwayFromZero);

        return (centerLatitude, centerLongitude);
    }

    // Standard geohash base32 encode at the given precision (number of characters). Exposed as a
    // testable static so "known lat/lng -> known cell" can be checked against an independently
    // published reference value, the same cross-checking discipline DistrictBoundaryProviderTests
    // uses against Nominatim.
    public static string Encode(double latitude, double longitude, int precision)
    {
        var bits = ComputeBits(latitude, longitude, precision * BitsPerChar);

        var hash = new char[precision];
        for (var charIndex = 0; charIndex < precision; charIndex++)
        {
            var value = 0;
            for (var bitInChar = 0; bitInChar < BitsPerChar; bitInChar++)
            {
                value = (value << 1) | bits[(charIndex * BitsPerChar) + bitInChar];
            }

            hash[charIndex] = Base32Alphabet[value];
        }

        return new string(hash);
    }

    // The bounding box of the geohash cell (at the given precision, in characters) containing the
    // point — i.e. every point inside these bounds encodes to the identical geohash string.
    internal static (double LatMin, double LatMax, double LonMin, double LonMax) GetCellBounds(
        double latitude, double longitude, int precision)
    {
        var totalBits = precision * BitsPerChar;

        double latMin = -90, latMax = 90;
        double lonMin = -180, lonMax = 180;
        var isLongitudeBit = true;

        for (var i = 0; i < totalBits; i++)
        {
            if (isLongitudeBit)
            {
                var mid = (lonMin + lonMax) / 2.0;
                if (longitude >= mid) lonMin = mid; else lonMax = mid;
            }
            else
            {
                var mid = (latMin + latMax) / 2.0;
                if (latitude >= mid) latMin = mid; else latMax = mid;
            }

            isLongitudeBit = !isLongitudeBit;
        }

        return (latMin, latMax, lonMin, lonMax);
    }

    // Same bisection as GetCellBounds, but recording which half was chosen at each step (1 = upper
    // half) instead of only the final bounds — that bit sequence is what Encode groups into base32.
    private static int[] ComputeBits(double latitude, double longitude, int totalBits)
    {
        var bits = new int[totalBits];

        double latMin = -90, latMax = 90;
        double lonMin = -180, lonMax = 180;
        var isLongitudeBit = true;

        for (var i = 0; i < totalBits; i++)
        {
            if (isLongitudeBit)
            {
                var mid = (lonMin + lonMax) / 2.0;
                if (longitude >= mid) { bits[i] = 1; lonMin = mid; } else { bits[i] = 0; lonMax = mid; }
            }
            else
            {
                var mid = (latMin + latMax) / 2.0;
                if (latitude >= mid) { bits[i] = 1; latMin = mid; } else { bits[i] = 0; latMax = mid; }
            }

            isLongitudeBit = !isLongitudeBit;
        }

        return bits;
    }
}
