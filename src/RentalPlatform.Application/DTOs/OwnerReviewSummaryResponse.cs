namespace RentalPlatform.Application.DTOs;

/// <summary>
/// Aggregate owner-review data for a user. There is no single "overall" score on
/// an owner review, so <see cref="OverallAverage"/> is the mean of the three
/// subscores across all reviews, and the distribution buckets each review's
/// rounded subscore mean.
/// </summary>
public sealed class OwnerReviewSummaryResponse
{
    public int ReviewCount { get; init; }
    public bool HasAggregate { get; init; }

    public double OverallAverage { get; init; }
    public double CommunicationAverage { get; init; }
    public double PickupHandoverAverage { get; init; }
    public double FriendlinessAverage { get; init; }

    /// <summary>Counts of rounded overall ratings, index 0 = 1★ … index 4 = 5★.</summary>
    public IReadOnlyList<int> Distribution { get; init; } = new int[5];

    public IReadOnlyCollection<ReviewCommentResponse> Comments { get; init; } = Array.Empty<ReviewCommentResponse>();
}
