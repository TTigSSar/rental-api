using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface IFavoritesService
{
    Task<ServiceResult<IReadOnlyCollection<ListingPreviewResponse>>> GetMineAsync(CancellationToken cancellationToken = default);
    Task<ServiceResult<bool>> AddAsync(Guid listingId, CancellationToken cancellationToken = default);
    Task<ServiceResult<bool>> RemoveAsync(Guid listingId, CancellationToken cancellationToken = default);
}
