using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface IReviewsService
{
    Task<ServiceResult<ReviewResponse>> CreateAsync(
        CreateReviewRequest request,
        CancellationToken cancellationToken = default);

    Task<ServiceResult<IReadOnlyCollection<ReviewResponse>>> GetByListingAsync(
        Guid listingId,
        CancellationToken cancellationToken = default);

    Task<ServiceResult<IReadOnlyCollection<ReviewResponse>>> GetByUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}
