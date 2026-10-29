namespace RentalPlatform.Application.Common;

// Which of the 12 Yerevan districts share a border (ADR-054: a constant in code, no table). Derived
// from Infrastructure/Resources/yerevan-districts.geojson: two districts are neighbours when their
// boundaries share at least three vertices. Symmetric by construction; a unit test pins that and the
// coverage of all 12 district codes.
public static class YerevanDistrictAdjacency
{
    private static readonly IReadOnlyDictionary<string, string[]> Map = new Dictionary<string, string[]>
    {
        ["ajapnyak"] = new[] { "arabkir", "davtashen", "kentron", "malatia-sebastia" },
        ["arabkir"] = new[] { "ajapnyak", "davtashen", "kanaker-zeytun", "kentron" },
        ["avan"] = new[] { "kanaker-zeytun", "nor-nork" },
        ["davtashen"] = new[] { "ajapnyak", "arabkir" },
        ["erebuni"] = new[] { "kentron", "nork-marash", "nor-nork", "nubarashen", "shengavit" },
        ["kanaker-zeytun"] = new[] { "arabkir", "avan", "kentron", "nor-nork" },
        ["kentron"] = new[] { "ajapnyak", "arabkir", "erebuni", "kanaker-zeytun", "malatia-sebastia", "nork-marash", "nor-nork", "shengavit" },
        ["malatia-sebastia"] = new[] { "ajapnyak", "kentron", "shengavit" },
        ["nork-marash"] = new[] { "erebuni", "kentron", "nor-nork" },
        ["nor-nork"] = new[] { "avan", "erebuni", "kanaker-zeytun", "kentron", "nork-marash" },
        ["nubarashen"] = new[] { "erebuni", "shengavit" },
        ["shengavit"] = new[] { "erebuni", "kentron", "malatia-sebastia", "nubarashen" }
    };

    public static IReadOnlyCollection<string> Codes => Map.Keys.ToArray();

    public static IReadOnlyList<string> NeighboursOf(string code) =>
        Map.TryGetValue(code, out var neighbours) ? neighbours : Array.Empty<string>();
}
