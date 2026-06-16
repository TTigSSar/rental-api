namespace RentalPlatform.Application.DTOs;

public sealed class ListingPreviewResponse
{
    public Guid Id { get; init; }
    public Guid CategoryId { get; init; }
    public string CategoryName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public decimal PricePerDay { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public string? PrimaryImageUrl { get; init; }
    public int? AgeFromMonths { get; init; }
    public int? AgeToMonths { get; init; }
    public string? Condition { get; init; }
    public DateTime CreatedAt { get; init; }

    /// <summary>Average toy rating, or null when below the aggregate threshold.</summary>
    public double? Rating { get; init; }
    public int ReviewCount { get; init; }
}
