using System.Text.Json;
using RentalPlatform.Infrastructure.Services;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// Sanity tests for the yerevan-districts.geojson embedded asset (P0-2) and the
// DistrictBoundaryProvider point-in-polygon lookup built on top of it. Reference points were
// picked from well-known Yerevan locations and independently cross-checked against Nominatim
// reverse geocoding (nominatim.openstreetmap.org/reverse) — not just against this file's own
// polygons — before being baked in here as expectations.
public sealed class DistrictBoundaryProviderTests
{
    private const string ResourceName = "RentalPlatform.Infrastructure.Resources.yerevan-districts.geojson";

    private static string LoadRawGeoJson()
    {
        var assembly = typeof(DistrictBoundaryProvider).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }

    [Fact]
    public void EmbeddedAsset_Loads_And_Has_Exactly_12_Districts_With_All_Properties_Populated()
    {
        var json = LoadRawGeoJson();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("FeatureCollection", root.GetProperty("type").GetString());

        var features = root.GetProperty("features");
        Assert.Equal(12, features.GetArrayLength());

        var seenCodes = new HashSet<string>();
        foreach (var feature in features.EnumerateArray())
        {
            var props = feature.GetProperty("properties");

            var code = props.GetProperty("code").GetString();
            Assert.False(string.IsNullOrWhiteSpace(code));
            Assert.True(seenCodes.Add(code!), $"Duplicate district code: {code}");

            Assert.False(string.IsNullOrWhiteSpace(props.GetProperty("nameEn").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(props.GetProperty("nameHy").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(props.GetProperty("nameRu").GetString()));
            Assert.True(props.GetProperty("osmRelationId").GetInt64() > 0);

            var geometryType = feature.GetProperty("geometry").GetProperty("type").GetString();
            Assert.True(geometryType is "Polygon" or "MultiPolygon");
        }

        var expectedCodes = new[]
        {
            "ajapnyak", "arabkir", "avan", "davtashen", "erebuni", "kanaker-zeytun",
            "kentron", "malatia-sebastia", "nork-marash", "nor-nork", "nubarashen", "shengavit",
        };
        foreach (var expected in expectedCodes)
        {
            Assert.Contains(expected, seenCodes);
        }
    }

    [Theory]
    [InlineData(40.1776, 44.5126, "kentron")] // Republic Square
    [InlineData(40.1650, 44.4600, "malatia-sebastia")] // west-side Malatia-Sebastia
    [InlineData(40.1990, 44.4970, "arabkir")] // central Arabkir
    [InlineData(40.1830, 44.5700, "nor-nork")] // Nor Nork massif
    public void FindDistrictCode_Returns_Expected_District_For_Known_Points(double latitude, double longitude, string expectedCode)
    {
        var provider = new DistrictBoundaryProvider();

        var result = provider.FindDistrictCode(latitude, longitude);

        Assert.Equal(expectedCode, result);
    }

    [Fact]
    public void FindDistrictCode_Returns_Null_For_A_Point_Far_Outside_Yerevan()
    {
        var provider = new DistrictBoundaryProvider();

        var result = provider.FindDistrictCode(40.0, 45.5);

        Assert.Null(result);
    }
}
