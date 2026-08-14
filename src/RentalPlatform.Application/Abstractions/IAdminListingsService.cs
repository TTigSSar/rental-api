using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface IAdminListingsService
{
    Task<ServiceResult<IReadOnlyCollection<PendingListingForReviewResponse>>> GetPendingAsync(
        CancellationToken cancellationToken = default);

    Task<ServiceResult<AdminListingQueueResponse>> GetQueueAsync(
        AdminListingQueueFilter filter,
        CancellationToken cancellationToken = default);

    Task<ServiceResult<AdminListingDetailResponse>> GetDetailAsync(
        Guid listingId,
        CancellationToken cancellationToken = default);

    Task<ServiceResult<AdminListingDetailResponse>> UpdateCategoryAsync(
        Guid listingId,
        Guid categoryId,
        CancellationToken cancellationToken = default);

    Task<ServiceResult<ModerateListingResponse>> ApproveAsync(
        Guid listingId,
        CancellationToken cancellationToken = default);

    Task<ServiceResult<ModerateListingResponse>> RejectAsync(
        Guid listingId,
        string reasonCode,
        string? note,
        CancellationToken cancellationToken = default);
}
