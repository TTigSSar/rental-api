namespace RentalPlatform.Domain.Entities;

/// <summary>
/// One AI search (page 1 only; ADR-054). Metadata is kept indefinitely; the raw
/// <see cref="Description"/> is nulled after 30 days. The daily budget is the sum of
/// <see cref="CostUsd"/> since Yerevan midnight.
/// </summary>
public sealed class AiSearchLog
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? Description { get; set; }
    public string? ParseModel { get; set; }
    public string? RankModel { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public decimal CostUsd { get; set; }
    public int LatencyMs { get; set; }

    /// <summary>ok | ranking_failed | ai_unavailable | budget_exhausted (see AiSearchOutcomes).</summary>
    public string Outcome { get; set; } = string.Empty;

    public User? User { get; set; }
}
