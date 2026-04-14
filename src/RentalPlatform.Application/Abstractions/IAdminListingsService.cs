using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface IAdminListingsService
{
    Task<ServiceResult<IReadOnlyCollection<AdminPendingListingResponse>>> GetPendingAsync(CancellationToken cancellationToken = default);
    Task<ServiceResult<AdminPendingListingResponse>> ApproveAsync(Guid listingId, CancellationToken cancellationToken = default);
    Task<ServiceResult<AdminPendingListingResponse>> RejectAsync(Guid listingId, CancellationToken cancellationToken = default);
}
