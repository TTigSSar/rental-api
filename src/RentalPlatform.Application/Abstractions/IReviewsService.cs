using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface IReviewsService
{
    Task<ServiceResult<BookingReviewStatusResponse>> SubmitToyReviewAsync(
        CreateToyReviewRequest request, CancellationToken cancellationToken = default);

    Task<ServiceResult<BookingReviewStatusResponse>> SubmitOwnerReviewAsync(
        CreateOwnerReviewRequest request, CancellationToken cancellationToken = default);

    Task<ServiceResult<BookingReviewStatusResponse>> SubmitRenterReviewAsync(
        CreateRenterReviewRequest request, CancellationToken cancellationToken = default);

    Task<ServiceResult<BookingReviewStatusResponse>> GetBookingReviewStatusAsync(
        Guid bookingId, CancellationToken cancellationToken = default);

    Task<ToyReviewSummaryResponse> GetListingToyReviewsAsync(
        Guid listingId, CancellationToken cancellationToken = default);

    /// <summary>Null when <paramref name="ownerId"/> is a deleted user (ADR-056): the API answers 404.</summary>
    Task<OwnerReviewSummaryResponse?> GetOwnerReviewsAsync(
        Guid ownerId, CancellationToken cancellationToken = default);

    /// <summary>Null when <paramref name="renterId"/> is a deleted user (ADR-056): the API answers 404.</summary>
    Task<RenterReviewSummaryResponse?> GetRenterReviewsAsync(
        Guid renterId, CancellationToken cancellationToken = default);
}
