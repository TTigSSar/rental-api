using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface IListingImagesOwnerService
{
    Task<ServiceResult<IReadOnlyCollection<ListingImageResponse>>> UploadAsync(
        Guid listingId,
        IReadOnlyCollection<UploadListingImageRequest> files,
        CancellationToken cancellationToken = default);

    // Removes a single listing image. If the removed image was primary, the next
    // image (by SortOrder) is promoted. Returns the listing's remaining images.
    Task<ServiceResult<IReadOnlyCollection<ListingImageResponse>>> DeleteAsync(
        Guid listingId,
        Guid imageId,
        CancellationToken cancellationToken = default);
}
