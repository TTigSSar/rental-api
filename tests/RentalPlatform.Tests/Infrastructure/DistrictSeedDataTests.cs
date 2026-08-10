using System.Text.Json;
using RentalPlatform.Infrastructure.DependencyInjection.DevelopmentSeed;
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

    // Guards every Yerevan-city dev-seed listing's (Latitude, Longitude) against the district
    // polygon it is meant to render in. SeedListing carries no district field (DistrictId,
    // PublicLatitude and PublicLongitude are all derived at startup by
    // ListingLocationBackfillRunner), so this table is the only place the intended district per
    // listing is recorded — it must be kept in sync whenever a Yerevan listing's coordinates
    // change. The single non-Yerevan listing (Birthday Party Toy Pack, Gyumri) legitimately
    // resolves to no district and is intentionally excluded below.
    private static readonly (Guid ListingId, string ExpectedDistrictCode)[] ExpectedYerevanListingDistricts =
    [
        // ---- Original toy-MVP cohort (pre-2026-08) ----
        (DevelopmentSeedData.ListingIds.LegoDuploStarterSet, "kentron"),
        (DevelopmentSeedData.ListingIds.MontessoriWoodenToySet, "arabkir"),
        (DevelopmentSeedData.ListingIds.BabyActivityGym, "kentron"),
        (DevelopmentSeedData.ListingIds.KidsBalanceBike, "nork-marash"),
        (DevelopmentSeedData.ListingIds.OutdoorBackyardSlide, "malatia-sebastia"),
        (DevelopmentSeedData.ListingIds.ChildrensPuzzleBundle, "kentron"),
        (DevelopmentSeedData.ListingIds.ToyKitchenSet, "kentron"),
        (DevelopmentSeedData.ListingIds.BoardGameFamilyBundle, "arabkir"),
        // BirthdayPartyToyPack (Gyumri) intentionally excluded — resolves to null, see comment above.
        (DevelopmentSeedData.ListingIds.SoftPlayFoamSet, "kentron"),
        (DevelopmentSeedData.ListingIds.StemScienceDiscoveryKit, "nor-nork"),
        (DevelopmentSeedData.ListingIds.ClassicBoardGameTrio, "davtashen"),
        (DevelopmentSeedData.ListingIds.PartyFunActivityPack, "arabkir"),
        (DevelopmentSeedData.ListingIds.WoodenTrainSet, "shengavit"),
        (DevelopmentSeedData.ListingIds.KidsArtEasel, "erebuni"),

        // ---- 50-listing toy-catalogue expansion (2026-08) ----
        (DevelopmentSeedData.ListingIds.FisherPrice4In1OceanWondersBouncer, "ajapnyak"),
        (DevelopmentSeedData.ListingIds.LEGOClassicCreativeBricksBox500Pcs, "arabkir"),
        (DevelopmentSeedData.ListingIds.LeapFrogLeapStartInteractiveLearningSystem, "avan"),
        (DevelopmentSeedData.ListingIds.SmobyOutdoorPlayhouse, "davtashen"),
        (DevelopmentSeedData.ListingIds.LittleTikesCozyCoupe, "erebuni"),
        (DevelopmentSeedData.ListingIds.MelissaDougWoodenDollhouse, "kanaker-zeytun"),
        (DevelopmentSeedData.ListingIds.HapePoundTapBench, "kentron"),
        (DevelopmentSeedData.ListingIds.Djeco100PieceFloorPuzzleFamilyReunion, "malatia-sebastia"),
        (DevelopmentSeedData.ListingIds.HasbroGuessWhoClassic, "nork-marash"),
        (DevelopmentSeedData.ListingIds.LittleTikesInflatableBounceHouse, "nor-nork"),
        (DevelopmentSeedData.ListingIds.ChiccoBabySensesActivityGym, "nubarashen"),
        (DevelopmentSeedData.ListingIds.MegaBloksFirstBuildersBigBuildingBag80Pcs, "shengavit"),
        (DevelopmentSeedData.ListingIds.LearningResourcesCodingCrittersRangerZip, "ajapnyak"),
        (DevelopmentSeedData.ListingIds.IntexInflatableKiddiePoolOceanPlayCenter, "arabkir"),
        (DevelopmentSeedData.ListingIds.RadioFlyerClassicRedTricycle, "avan"),
        (DevelopmentSeedData.ListingIds.Step2FixerUpperToolBench, "davtashen"),
        (DevelopmentSeedData.ListingIds.MelissaDougShapeSortingCube, "erebuni"),
        (DevelopmentSeedData.ListingIds.Ravensburger200PieceDisneyPuzzle, "kanaker-zeytun"),
        (DevelopmentSeedData.ListingIds.HabaMyVeryFirstGamesOrchardCompare, "kentron"),
        (DevelopmentSeedData.ListingIds.IntexBallPitWith100Balls, "malatia-sebastia"),
        (DevelopmentSeedData.ListingIds.TinyLoveMeadowDaysGyminiPlayMat, "nork-marash"),
        (DevelopmentSeedData.ListingIds.LEGOCityFireStationPlayset, "nor-nork"),
        (DevelopmentSeedData.ListingIds.VTechAlphabetTrain, "nubarashen"),
        (DevelopmentSeedData.ListingIds.LittleTikes45FootTrampoline, "shengavit"),
        (DevelopmentSeedData.ListingIds.RazorJrLilKickScooter, "ajapnyak"),
        (DevelopmentSeedData.ListingIds.KidKraftVintageKitchenPlayset, "arabkir"),
        (DevelopmentSeedData.ListingIds.GrimmsWoodenRainbowStacker, "avan"),
        (DevelopmentSeedData.ListingIds.MelissaDougWoodenPegPuzzleFarmAnimals, "davtashen"),
        (DevelopmentSeedData.ListingIds.RavensburgerLabyrinthJunior, "erebuni"),
        (DevelopmentSeedData.ListingIds.KidsKaraokeMachineWithDiscoLights, "kanaker-zeytun"),
        (DevelopmentSeedData.ListingIds.VTechSitToStandLearningWalker, "kentron"),
        (DevelopmentSeedData.ListingIds.MagnaTilesClearColors32PieceSet, "malatia-sebastia"),
        (DevelopmentSeedData.ListingIds.MelissaDougWoodenAlphabetPuzzleBoard, "nork-marash"),
        (DevelopmentSeedData.ListingIds.Step2NaturallyPlayfulSandTable, "nor-nork"),
        (DevelopmentSeedData.ListingIds.PegPeregoJohnDeereGroundForceRideOnTractor, "nubarashen"),
        (DevelopmentSeedData.ListingIds.FisherPriceLittlePeopleFarm, "shengavit"),
        (DevelopmentSeedData.ListingIds.PlanToysWoodenSortingBoard, "ajapnyak"),
        (DevelopmentSeedData.ListingIds.Educa300PieceKidsPuzzleDinosaurs, "arabkir"),
        (DevelopmentSeedData.ListingIds.CatanJunior, "avan"),
        (DevelopmentSeedData.ListingIds.NerfRivalPartyBlasterSetX4, "davtashen"),
        (DevelopmentSeedData.ListingIds.MunchkinBathToyOrganizerSquirtersSet, "erebuni"),
        (DevelopmentSeedData.ListingIds.LEGOTechnicOffRoadBuggy, "kanaker-zeytun"),
        (DevelopmentSeedData.ListingIds.OsmoGeniusStarterKitForIPad, "kentron"),
        (DevelopmentSeedData.ListingIds.HedstromRainbowWaterSprinklerPlayMat, "malatia-sebastia"),
        (DevelopmentSeedData.ListingIds.Strider12SportBalanceBike, "nork-marash"),
        (DevelopmentSeedData.ListingIds.PlaymobilGrandCastlePlayset, "nor-nork"),
        (DevelopmentSeedData.ListingIds.LoveveryPlayKitTheBabbler, "nubarashen"),
        (DevelopmentSeedData.ListingIds.JanodMagneticWoodenPuzzleBookSeasons, "shengavit"),
        (DevelopmentSeedData.ListingIds.JengaClassicWoodenBlockGame, "arabkir"),
        (DevelopmentSeedData.ListingIds.PinataPartyFavorBundle, "kentron"),
    ];

    [Fact]
    public void Every_Yerevan_Seed_Listing_Resolves_To_Its_Expected_District()
    {
        var provider = new DistrictBoundaryProvider();
        var expectedById = ExpectedYerevanListingDistricts.ToDictionary(x => x.ListingId, x => x.ExpectedDistrictCode);

        var yerevanListings = DevelopmentSeedData.Listings.Where(l => l.City == "Yerevan").ToList();

        // Every Yerevan listing must have an entry in the table above (and vice versa) so the
        // table can't silently drift out of sync as listings are added.
        var yerevanIds = yerevanListings.Select(l => l.Id).ToHashSet();
        var missingFromTable = yerevanIds.Except(expectedById.Keys).ToList();
        Assert.True(missingFromTable.Count == 0,
            $"{missingFromTable.Count} Yerevan listing(s) have no expected-district entry in the test table: {string.Join(", ", missingFromTable)}");

        var extraInTable = expectedById.Keys.Except(yerevanIds).ToList();
        Assert.True(extraInTable.Count == 0,
            $"{extraInTable.Count} listing id(s) in the test table no longer correspond to a Yerevan seed listing: {string.Join(", ", extraInTable)}");

        foreach (var listing in yerevanListings)
        {
            var expectedCode = expectedById[listing.Id];
            var actualCode = provider.FindDistrictCode((double)listing.Latitude, (double)listing.Longitude);
            Assert.True(actualCode == expectedCode,
                $"Listing '{listing.Title}' ({listing.Id}) at ({listing.Latitude}, {listing.Longitude}) " +
                $"resolved to district '{actualCode ?? "<null>"}' but expected '{expectedCode}'.");
        }
    }

    [Fact]
    public void The_NonYerevan_Seed_Listing_Resolves_To_No_District()
    {
        var provider = new DistrictBoundaryProvider();
        var gyumriListings = DevelopmentSeedData.Listings.Where(l => l.City != "Yerevan").ToList();

        Assert.Single(gyumriListings);
        var listing = gyumriListings[0];
        var actualCode = provider.FindDistrictCode((double)listing.Latitude, (double)listing.Longitude);
        Assert.Null(actualCode);
    }
}
