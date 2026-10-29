namespace RentalPlatform.Application.Common;

// Stable ai_search.* error codes (ADR-045, ADR-054). The client matches on the exact string.
public static class AiSearchErrorCodes
{
    public const string Unauthenticated = "ai_search.unauthenticated";
    public const string InvalidDescription = "ai_search.invalid_description";
    public const string RateLimited = "ai_search.rate_limited";
    public const string DailyLimit = "ai_search.daily_limit";
}

public static class AiSearchOutcomes
{
    public const string Ok = "ok";
    public const string RankingFailed = "ranking_failed";
    public const string AiUnavailable = "ai_unavailable";
    public const string BudgetExhausted = "budget_exhausted";
}
