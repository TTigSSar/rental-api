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

    // Atomically replaces ALL existing images with the supplied set.
    // The first file becomes primary (SortOrder 0). Old physical files are
    // deleted best-effort after the DB commit. The listing is reset to
    // PendingApproval so moderation sees the new images before re-publishing.
    Task<ServiceResult<IReadOnlyCollection<ListingImageResponse>>> ReplaceAsync(
        Guid listingId,
        IReadOnlyCollection<UploadListingImageRequest> files,
        CancellationToken cancellationToken = default);
}
