namespace RentalPlatform.Application.DTOs;

public sealed class CategoryResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string? IconName { get; init; }
    public string? ImageUrl { get; init; }
    public int DisplayOrder { get; init; }
}
