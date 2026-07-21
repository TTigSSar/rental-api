using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface IDistrictsQueryService
{
    Task<IReadOnlyCollection<ListingDistrictResponse>> GetAllAsync(CancellationToken cancellationToken = default);
}
