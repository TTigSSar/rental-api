using System.Text.Json;
using RentalPlatform.Application.Abstractions;

namespace RentalPlatform.Infrastructure.Services;

// Loads the embedded yerevan-districts.geojson asset once (Lazy, thread-safe) and answers
// point-in-polygon district lookups against it via plain ray-casting. Handles polygon holes
// and MultiPolygon (Malatia-Sebastia has two disjoint outer rings in the source OSM data).
// No geometry NuGet package — this is intentionally a minimal, dependency-free implementation.
public sealed class DistrictBoundaryProvider : IDistrictBoundaryProvider
{
    private const string ResourceName = "RentalPlatform.Infrastructure.Resources.yerevan-districts.geojson";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Lazy<IReadOnlyList<DistrictFeature>> _features;

    public DistrictBoundaryProvider()
    {
        _features = new Lazy<IReadOnlyList<DistrictFeature>>(LoadFeatures, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string? FindDistrictCode(double latitude, double longitude)
    {
        foreach (var feature in _features.Value)
        {
            if (feature.Contains(longitude, latitude))
            {
                return feature.Code;
            }
        }

        return null;
    }

    private static IReadOnlyList<DistrictFeature> LoadFeatures()
    {
        var assembly = typeof(DistrictBoundaryProvider).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();

        var raw = JsonSerializer.Deserialize<GeoJsonFeatureCollection>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to parse yerevan-districts.geojson: empty document.");

        var features = new List<DistrictFeature>(raw.Features.Count);
        foreach (var f in raw.Features)
        {
            features.Add(new DistrictFeature(
                f.Properties.Code,
                ExtractPolygons(f.Geometry)));
        }

        return features;
    }

    private static List<PolygonRings> ExtractPolygons(GeoJsonGeometry geometry)
    {
        var polygons = new List<PolygonRings>();
        switch (geometry.Type)
        {
            case "Polygon":
                polygons.Add(new PolygonRings(ParseRings(geometry.Coordinates)));
                break;
            case "MultiPolygon":
                foreach (var polygonElement in geometry.Coordinates.EnumerateArray())
                {
                    polygons.Add(new PolygonRings(ParseRings(polygonElement)));
                }
                break;
            default:
                throw new InvalidOperationException($"Unsupported geometry type '{geometry.Type}' in yerevan-districts.geojson.");
        }

        return polygons;
    }

    private static List<Point[]> ParseRings(JsonElement ringsElement)
    {
        var rings = new List<Point[]>();
        foreach (var ringElement in ringsElement.EnumerateArray())
        {
            var points = new List<Point>();
            foreach (var coordElement in ringElement.EnumerateArray())
            {
                // GeoJSON position order is [longitude, latitude].
                var lon = coordElement[0].GetDouble();
                var lat = coordElement[1].GetDouble();
                points.Add(new Point(lon, lat));
            }

            rings.Add(points.ToArray());
        }

        return rings;
    }

    private sealed class GeoJsonFeatureCollection
    {
        public List<GeoJsonFeature> Features { get; set; } = new();
    }

    private sealed class GeoJsonFeature
    {
        public GeoJsonProperties Properties { get; set; } = new();
        public GeoJsonGeometry Geometry { get; set; } = new();
    }

    private sealed class GeoJsonProperties
    {
        public string Code { get; set; } = "";
    }

    private sealed class GeoJsonGeometry
    {
        public string Type { get; set; } = "";

        // System.Text.Json supports JsonElement properties natively (defers parsing of the
        // variable-depth Polygon/MultiPolygon coordinate arrays to ExtractPolygons/ParseRings).
        public JsonElement Coordinates { get; set; }
    }

    private readonly record struct Point(double Lon, double Lat);

    private sealed class PolygonRings
    {
        // Rings[0] is the exterior ring; any further rings are holes.
        public IReadOnlyList<Point[]> Rings { get; }

        public PolygonRings(IReadOnlyList<Point[]> rings)
        {
            Rings = rings;
        }
    }

    private sealed class DistrictFeature
    {
        public string Code { get; }
        private readonly IReadOnlyList<PolygonRings> _polygons;

        public DistrictFeature(string code, IReadOnlyList<PolygonRings> polygons)
        {
            Code = code;
            _polygons = polygons;
        }

        public bool Contains(double lon, double lat)
        {
            foreach (var polygon in _polygons)
            {
                if (ContainsInPolygon(polygon, lon, lat))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsInPolygon(PolygonRings polygon, double lon, double lat)
        {
            if (polygon.Rings.Count == 0 || !IsInRing(polygon.Rings[0], lon, lat))
            {
                return false;
            }

            for (var i = 1; i < polygon.Rings.Count; i++)
            {
                if (IsInRing(polygon.Rings[i], lon, lat))
                {
                    // Inside a hole => not actually inside this polygon.
                    return false;
                }
            }

            return true;
        }

        // Standard ray-casting (PNPOLY) point-in-polygon test.
        private static bool IsInRing(Point[] ring, double lon, double lat)
        {
            var inside = false;
            for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
            {
                var pi = ring[i];
                var pj = ring[j];

                var straddles = (pi.Lat > lat) != (pj.Lat > lat);
                if (!straddles)
                {
                    continue;
                }

                var xIntersect = (pj.Lon - pi.Lon) * (lat - pi.Lat) / (pj.Lat - pi.Lat) + pi.Lon;
                if (lon < xIntersect)
                {
                    inside = !inside;
                }
            }

            return inside;
        }
    }
}
