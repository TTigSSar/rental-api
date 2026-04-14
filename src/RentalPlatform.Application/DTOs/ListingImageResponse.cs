namespace RentalPlatform.Application.DTOs;

public sealed class ListingImageResponse
{
    public Guid Id { get; init; }
    public string Url { get; init; } = string.Empty;
    public bool IsPrimary { get; init; }
    public int SortOrder { get; init; }
}
