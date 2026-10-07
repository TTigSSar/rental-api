using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface IDistrictsQueryService
{
    Task<IReadOnlyCollection<ListingDistrictResponse>> GetAllAsync(CancellationToken cancellationToken = default);

    // Point-in-polygon lookup for an arbitrary WGS84 coordinate (see IDistrictBoundaryProvider).
    // Never fails on an out-of-area point: a coordinate outside every Yerevan district simply
    // returns a null district, so the caller renders "outside the service area" rather than an
    // error. That null is the same verdict the write side gives (auth.home_point_outside_yerevan).
    Task<DistrictAtResponse> FindAtAsync(decimal latitude, decimal longitude, CancellationToken cancellationToken = default);
}
