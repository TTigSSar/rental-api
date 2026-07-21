using System.Text.Json;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// Guards the Districts seed data (DistrictConfiguration.HasData, migration
// AddDistrictsAndListingLocationFields) against transcription drift from its source of truth,
// Infrastructure/Resources/yerevan-districts.geojson. Rather than trust a hand-retyped seed list
// against a hand-read GeoJSON excerpt, this test parses the SAME embedded asset
// DistrictBoundaryProvider reads and compares it directly to what actually lands in the database
// via EF's HasData + EnsureCreated.
public sealed class DistrictSeedDataTests
{
    private const string ResourceName = "RentalPlatform.Infrastructure.Resources.yerevan-districts.geojson";

    private sealed record ExpectedDistrict(string Code, string NameEn, string NameHy, string NameRu);

    private static List<ExpectedDistrict> LoadExpectedDistrictsFromGeoJson()
    {
        var assembly = typeof(DistrictBoundaryProvider).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();

        using var doc = JsonDocument.Parse(json);
        var expected = new List<ExpectedDistrict>();
        foreach (var feature in doc.RootElement.GetProperty("features").EnumerateArray())
        {
            var props = feature.GetProperty("properties");
            expected.Add(new ExpectedDistrict(
                props.GetProperty("code").GetString()!,
                props.GetProperty("nameEn").GetString()!,
                props.GetProperty("nameHy").GetString()!,
                props.GetProperty("nameRu").GetString()!));
        }

        return expected;
    }

    [Fact]
    public async Task Seeded_Districts_Match_The_GeoJson_Asset_Exactly()
    {
        var expectedDistricts = LoadExpectedDistrictsFromGeoJson();
        Assert.Equal(12, expectedDistricts.Count);

        using var database = new SqliteTestDatabase();
        await using var context = database.CreateContext();
        var seededDistricts = context.Districts.ToList();

        Assert.Equal(12, seededDistricts.Count);

        var seededByCode = seededDistricts.ToDictionary(d => d.Code);
        foreach (var expected in expectedDistricts)
        {
            Assert.True(seededByCode.TryGetValue(expected.Code, out var seeded),
                $"No seeded District row found for code '{expected.Code}'.");
            Assert.Equal(expected.NameEn, seeded!.NameEn);
            Assert.Equal(expected.NameHy, seeded.NameHy);
            Assert.Equal(expected.NameRu, seeded.NameRu);
            Assert.NotEqual(Guid.Empty, seeded.Id);
        }

        // Ids are hard-coded/stable — every row's Id must be distinct.
        Assert.Equal(12, seededDistricts.Select(d => d.Id).Distinct().Count());
    }
}
