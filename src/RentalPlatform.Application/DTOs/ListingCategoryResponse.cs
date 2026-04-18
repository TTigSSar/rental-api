namespace RentalPlatform.Application.DTOs;

public sealed class ListingCategoryResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
}
