using System.Text.Json;
using RentalPlatform.Application.Abstractions;

namespace RentalPlatform.Tests.TestSupport;

// Scripted stand-in for the LLM seam (ADR-054). Counts calls, records every input verbatim (so a test
// can prove what did and did not cross the boundary) and returns whatever the test scripted. A script
// returning null is a model failure.
public sealed class FakeAiSearchModel : IAiSearchModel
{
    public int ParseCalls { get; private set; }
    public int RankCalls { get; private set; }

    /// <summary>Everything handed to the model, serialized, in call order.</summary>
    public List<string> Inputs { get; } = new();

    public Func<string, string, AiSearchCatalogs, AiParsedQuery?> OnParse { get; set; } = (_, _, _) => null;
    public Func<string, IReadOnlyList<AiRankCandidate>, IReadOnlyList<Guid>?> OnRank { get; set; } = (_, _) => null;

    public decimal ParseCostUsd { get; set; }
    public decimal RankCostUsd { get; set; }

    /// <summary>When set, a scripted null is a billed-but-unusable call (a Failed result with this cost), not a free failure.</summary>
    public decimal? ParseFailedCostUsd { get; set; }
    public decimal? RankFailedCostUsd { get; set; }

    /// <summary>Runs inside the model call, after it is recorded (e.g. to abort the request mid-call).</summary>
    public Action? OnParseCalled { get; set; }

    public void Reset()
    {
        ParseFailedCostUsd = null;
        RankFailedCostUsd = null;
        OnParseCalled = null;
        ParseCalls = 0;
        RankCalls = 0;
        Inputs.Clear();
        OnParse = (_, _, _) => null;
        OnRank = (_, _) => null;
        ParseCostUsd = 0;
        RankCostUsd = 0;
    }

    public Task<AiModelResult<AiParsedQuery>?> ParseAsync(
        string description, string language, AiSearchCatalogs catalogs, CancellationToken cancellationToken = default)
    {
        ParseCalls++;
        Inputs.Add(JsonSerializer.Serialize(new { description, language, catalogs.Categories, catalogs.Districts }));

        var parsed = OnParse(description, language, catalogs);
        OnParseCalled?.Invoke();
        return Task.FromResult<AiModelResult<AiParsedQuery>?>(
            parsed is not null ? new AiModelResult<AiParsedQuery>(parsed, "fake-parse", 100, 20, ParseCostUsd)
            : ParseFailedCostUsd is { } failed ? AiModelResult<AiParsedQuery>.Failed("fake-parse", 100, 20, failed)
            : null);
    }

    public Task<AiModelResult<IReadOnlyList<Guid>>?> RankAsync(
        string description, IReadOnlyList<AiRankCandidate> candidates, CancellationToken cancellationToken = default)
    {
        RankCalls++;
        Inputs.Add(JsonSerializer.Serialize(new { description, candidates }));

        var order = OnRank(description, candidates);
        return Task.FromResult<AiModelResult<IReadOnlyList<Guid>>?>(
            order is not null ? new AiModelResult<IReadOnlyList<Guid>>(order, "fake-rank", 200, 40, RankCostUsd)
            : RankFailedCostUsd is { } failed ? AiModelResult<IReadOnlyList<Guid>>.Failed("fake-rank", 200, 40, failed)
            : null);
    }

    public static AiParsedQuery Parsed(
        string? categorySlug = null,
        int? ageMonths = null,
        int? maxPrice = null,
        IReadOnlyList<string>? districts = null,
        bool districtIsHard = false,
        bool nearMe = false,
        bool datesMentioned = false,
        string? itemType = null,
        string? purpose = null,
        string? intent = "Looking for a toy") =>
        new(ageMonths, categorySlug, maxPrice, maxPrice is not null, districts ?? Array.Empty<string>(),
            districtIsHard, nearMe, datesMentioned, itemType, purpose, intent);
}
