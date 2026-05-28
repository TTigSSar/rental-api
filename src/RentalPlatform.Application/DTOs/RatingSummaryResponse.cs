namespace RentalPlatform.Application.DTOs;

public sealed class RatingSummaryResponse
{
    public double AverageRating { get; init; }
    public int ReviewCount { get; init; }
}
