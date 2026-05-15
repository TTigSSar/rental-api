using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface IAdminListingsService
{
    Task<ServiceResult<IReadOnlyCollection<PendingListingForReviewResponse>>> GetPendingAsync(
        CancellationToken cancellationToken = default);

    Task<ServiceResult<ModerateListingResponse>> ApproveAsync(
        Guid listingId,
        CancellationToken cancellationToken = default);

    Task<ServiceResult<ModerateListingResponse>> RejectAsync(
        Guid listingId,
        string reason,
        CancellationToken cancellationToken = default);
}
