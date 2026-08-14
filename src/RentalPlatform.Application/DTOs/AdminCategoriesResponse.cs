namespace RentalPlatform.Application.DTOs;

public sealed class AdminCategoriesResponse
{
    public IReadOnlyCollection<AdminCategoryResponse> Items { get; init; } = Array.Empty<AdminCategoryResponse>();
    public AdminCategoriesSummary Summary { get; init; } = new();
}

/// <summary>Aggregates for the categories screen header. TotalListedToys is the sum of every
/// category's ListingCount (Approved-only), i.e. total approved listings that have a category —
/// which, since Listing.CategoryId is required, is every approved listing.</summary>
public sealed class AdminCategoriesSummary
{
    public int TotalCategories { get; init; }
    public int VisibleCount { get; init; }
    public int TotalListedToys { get; init; }
}
