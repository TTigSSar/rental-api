using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface IHomeSectionsService
{
    Task<HomeSectionsResponse> GetSectionsAsync(
        int itemsPerSection,
        CancellationToken cancellationToken = default);
}
