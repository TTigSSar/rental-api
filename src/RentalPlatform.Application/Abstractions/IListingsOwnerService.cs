using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface IListingsOwnerService
{
    Task<ServiceResult<CreateListingResponse>> CreateAsync(
        CreateListingRequest request,
        CancellationToken cancellationToken = default);

    Task<ServiceResult<IReadOnlyCollection<MyListingResponse>>> GetMineAsync(
        CancellationToken cancellationToken = default);

    Task<ServiceResult<bool>> ArchiveAsync(
        Guid listingId,
        CancellationToken cancellationToken = default);

    Task<ServiceResult<Guid>> UpdateAsync(
        Guid listingId,
        UpdateListingRequest request,
        CancellationToken cancellationToken = default);
}
