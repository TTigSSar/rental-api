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

    // ---- Per-OWNER district coverage (home-point model) --------------------------------------
    //
    // Listings no longer carry coordinates: a listing's pin is its owner's home point. So the
    // question "does the seeded catalogue cover the map?" is now a question about OWNERS, and these
    // tests ask it of DevelopmentSeedData.Users / ExpectedOwnerHomeDistrictCodes rather than of the
    // listing rows.
    //
    // Three things have to hold, and each has its own test below:
    //   1. every seeded home point resolves to the district the seed says it does;
    //   2. all 12 Yerevan districts have an owner, plus one owner outside Yerevan — otherwise the
    //      catalogue map collapses toward a handful of pins;
    //   3. every listing's declared City agrees with its owner's home point, since City is the one
    //      location field the seed still writes by hand and nothing derives it.

    [Fact]
    public void Every_Seeded_Home_Point_Resolves_To_Its_Expected_District()
    {
        var provider = new DistrictBoundaryProvider();

        var ownersWithHome = DevelopmentSeedData.Users
            .Where(user => user.HomeLatitude is not null && user.HomeLongitude is not null)
            .ToList();

        // The expectations table and the seeded users must describe the same set of owners, so
        // neither can drift silently as owners are added or removed.
        var ownerEmails = ownersWithHome.Select(user => user.Email).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tableEmails = DevelopmentSeedData.ExpectedOwnerHomeDistrictCodes.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missingFromTable = ownerEmails.Except(tableEmails, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.True(missingFromTable.Count == 0,
            $"{missingFromTable.Count} seeded owner(s) with a home point have no expected-district entry: {string.Join(", ", missingFromTable)}");

        var extraInTable = tableEmails.Except(ownerEmails, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.True(extraInTable.Count == 0,
            $"{extraInTable.Count} expected-district entr(ies) no longer correspond to a seeded owner with a home point: {string.Join(", ", extraInTable)}");

        foreach (var owner in ownersWithHome)
        {
            var expectedCode = DevelopmentSeedData.ExpectedOwnerHomeDistrictCodes[owner.Email];
            var actualCode = provider.FindDistrictCode((double)owner.HomeLatitude!.Value, (double)owner.HomeLongitude!.Value);

            Assert.True(actualCode == expectedCode,
                $"Owner '{owner.Email}' home point ({owner.HomeLatitude}, {owner.HomeLongitude}) " +
                $"resolved to district '{actualCode ?? "<null>"}' but expected '{expectedCode ?? "<null>"}'.");
        }
    }

    // The catalogue is only spread across the map if its OWNERS are, so every district needs at
    // least one resident owner. And every seeded owner must resolve to SOME district: a home point
    // outside Yerevan can no longer be saved, so the seed runner — which applies these through the
    // real IHomePointService — would simply be refused and the owner would end up with no location.
    [Fact]
    public void Every_Seed_Owner_Lives_In_A_Real_District_And_Together_They_Cover_All_Twelve()
    {
        var codes = DevelopmentSeedData.ExpectedOwnerHomeDistrictCodes.Values.ToList();

        Assert.All(codes, code => Assert.False(string.IsNullOrWhiteSpace(code)));
        Assert.Equal(12, codes.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var expectedFromAsset = LoadExpectedDistrictsFromGeoJson().Select(district => district.Code);
        Assert.Empty(expectedFromAsset.Except(codes, StringComparer.OrdinalIgnoreCase));
    }

    // City is the one location field still authored by hand in the seed (nothing can derive a place
    // name — see ListingsOwnerService.ResolveCity), so it is the one that can contradict the pin.
    // Every owner is in Yerevan, so every listing says Yerevan.
    [Fact]
    public void Every_Seed_Listings_City_Agrees_With_Its_Owners_Home_Point()
    {
        foreach (var listing in DevelopmentSeedData.Listings)
        {
            Assert.True(
                DevelopmentSeedData.ExpectedOwnerHomeDistrictCodes.ContainsKey(listing.OwnerEmail),
                $"Listing '{listing.Title}' is owned by {listing.OwnerEmail}, who has no seeded home point — it would have no location at all.");

            Assert.True(listing.City == "Yerevan",
                $"Listing '{listing.Title}' says City '{listing.City}', but every seeded owner lives in Yerevan.");
        }
    }

    // Kept only as documentation of where each listing USED to sit before the home-point change —
    // the pre-migration district every one of these listings' own coordinates resolved to. Nothing
    // derives from it any more; the demo bootstrap reads the equivalent fact from the
    // ListingLocationsBeforeHomePoint snapshot table on a live database instead.
    private static readonly (Guid ListingId, string ExpectedDistrictCode)[] HistoricalYerevanListingDistricts =
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

    // The historical table still has to name districts that exist, otherwise it is a record of
    // something that never happened. Cheap guard on a piece of documentation whose only remaining
    // job is to be accurate.
    [Fact]
    public void The_Historical_Listing_District_Table_Names_Only_Real_Districts()
    {
        var known = LoadExpectedDistrictsFromGeoJson().Select(district => district.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (listingId, code) in HistoricalYerevanListingDistricts)
        {
            Assert.True(known.Contains(code),
                $"Historical district '{code}' recorded for listing {listingId} is not one of the 12 Yerevan districts.");
        }
    }
}
