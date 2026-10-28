namespace RentalPlatform.Infrastructure.Services;

// Bound from "AiSearch" (ADR-054). A missing ApiKey is not a startup failure: AI search simply reports
// itself unavailable and every search falls back to the plain catalogue. Only ApiKey is expected from
// the environment (compose maps an unset variable to ""), so it is the only string-typed value that
// may arrive empty; the numbers keep their defaults unless someone sets them on purpose.
public sealed class AiSearchOptions
{
    public const string SectionName = "AiSearch";

    /// <summary>Local-dev fallback when AiSearch:ApiKey is empty, so a developer machine works unconfigured.</summary>
    public const string ApiKeyFallbackEnvironmentVariable = "ANTHROPIC_API_KEY";

    public string? ApiKey { get; set; }

    public string ParseModel { get; init; } = "claude-haiku-5-5";
    public string RankModel { get; init; } = "claude-sonnet-5-5";

    public decimal DailyBudgetUsd { get; init; } = 1m;
    public int PerUserPerMinute { get; init; } = 5;
    public double NearMeRadiusKm { get; init; } = 1;

    // USD per million tokens (provider price list; ADR-054 amendment). Config values so a price change
    // is a deploy-time edit, not a code change.
    public decimal ParseInputUsdPerMTok { get; init; } = 0.10m;
    public decimal ParseOutputUsdPerMTok { get; init; } = 0.50m;
    public decimal RankInputUsdPerMTok { get; init; } = 2.00m;
    public decimal RankOutputUsdPerMTok { get; init; } = 10.00m;

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    public static bool IsValid(AiSearchOptions options) =>
        options.DailyBudgetUsd >= 0
        && options.PerUserPerMinute is >= 1 and <= 600
        && options.NearMeRadiusKm is >= 0.2 and <= 20
        && options.ParseInputUsdPerMTok >= 0 && options.ParseOutputUsdPerMTok >= 0
        && options.RankInputUsdPerMTok >= 0 && options.RankOutputUsdPerMTok >= 0
        && !string.IsNullOrWhiteSpace(options.ParseModel)
        && !string.IsNullOrWhiteSpace(options.RankModel);
}
