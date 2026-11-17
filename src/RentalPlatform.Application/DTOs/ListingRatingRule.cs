namespace RentalPlatform.Application.DTOs;

// ADR-064 §2: the aggregate rating is exposed only from 2 reviews on (rounded to 1 decimal).
public static class ListingRatingRule
{
    public const int MinReviewsForRating = 2;

    public static double? Exposed(int reviewCount, long ratingSum) =>
        reviewCount >= MinReviewsForRating ? Math.Round((double)ratingSum / reviewCount, 1) : null;
}
