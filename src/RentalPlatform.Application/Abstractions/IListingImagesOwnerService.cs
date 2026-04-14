using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface IListingImagesOwnerService
{
    Task<ServiceResult<IReadOnlyCollection<ListingImageResponse>>> UploadAsync(
        Guid listingId,
        IReadOnlyCollection<UploadListingImageRequest> files,
        CancellationToken cancellationToken = default);
}
