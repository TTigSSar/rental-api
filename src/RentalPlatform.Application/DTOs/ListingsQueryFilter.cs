namespace RentalPlatform.Application.DTOs;

public sealed class ListingsQueryFilter
{
    public string? City { get; init; }
    public Guid? CategoryId { get; init; }
    public decimal? MinPrice { get; init; }
    public decimal? MaxPrice { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}
