namespace RentalPlatform.Application.DTOs;

public sealed class HomeSectionResponse
{
    public string Key { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public IReadOnlyCollection<ListingPreviewResponse> Items { get; init; } = Array.Empty<ListingPreviewResponse>();
}
