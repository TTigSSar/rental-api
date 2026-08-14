namespace RentalPlatform.Application.DTOs;

/// <summary>
/// One row of the admin categories table. ListingCount is Approved-listings-only ("live listings
/// in this category" from a moderator's point of view) — computed with a grouped query, never
/// per-row (see AdminCategoriesStore.GetApprovedListingCountsAsync).
/// </summary>
public sealed class AdminCategoryResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string? IconName { get; init; }
    public string? ColorHex { get; init; }
    public int DisplayOrder { get; init; }
    public bool IsVisible { get; init; }
    public int ListingCount { get; init; }
}
