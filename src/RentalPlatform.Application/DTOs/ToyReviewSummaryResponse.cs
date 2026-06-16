namespace RentalPlatform.Application.DTOs;

/// <summary>
/// Aggregate toy-review data for a listing. Aggregates are only meaningful when
/// <see cref="HasAggregate"/> is true (at least the minimum number of reviews).
/// Comments are always returned regardless of the aggregate threshold.
/// </summary>
public sealed class ToyReviewSummaryResponse
{
    public int ReviewCount { get; init; }
    public bool HasAggregate { get; init; }

    public double OverallAverage { get; init; }
    public double ConditionAverage { get; init; }
    public double CleanlinessAverage { get; init; }
    public double ValueForMoneyAverage { get; init; }
    public double FunPlayValueAverage { get; init; }
    public double DescriptionAccuracyAverage { get; init; }

    /// <summary>Counts of overall ratings, index 0 = 1★ … index 4 = 5★.</summary>
    public IReadOnlyList<int> Distribution { get; init; } = new int[5];

    public IReadOnlyCollection<ReviewCommentResponse> Comments { get; init; } = Array.Empty<ReviewCommentResponse>();
}
