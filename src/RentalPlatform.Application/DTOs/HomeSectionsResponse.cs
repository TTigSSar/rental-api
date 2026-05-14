namespace RentalPlatform.Application.DTOs;

public sealed class HomeSectionsResponse
{
    public IReadOnlyCollection<HomeSectionResponse> Sections { get; init; } = Array.Empty<HomeSectionResponse>();
}
