namespace RentalPlatform.Application.DTOs;

/// <summary>
/// Aggregate renter-review data for a user (reputation as a renter).
/// <see cref="OverallAverage"/> is the mean of the four subscores across reviews.
/// </summary>
public sealed class RenterReviewSummaryResponse
{
    public int ReviewCount { get; init; }
    public bool HasAggregate { get; init; }

    public double OverallAverage { get; init; }
    public double CommunicationAverage { get; init; }
    public double ReturnedOnTimeAverage { get; init; }
    public double CareOfToyAverage { get; init; }
    public double WouldRentAgainAverage { get; init; }

    public IReadOnlyList<int> Distribution { get; init; } = new int[5];

    public IReadOnlyCollection<ReviewCommentResponse> Comments { get; init; } = Array.Empty<ReviewCommentResponse>();
}
