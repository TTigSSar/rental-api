using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface IBookingsService
{
    Task<ServiceResult<BookingResponse>> CreateAsync(CreateBookingRequest request, CancellationToken cancellationToken = default);
    Task<ServiceResult<IReadOnlyCollection<BookingResponse>>> GetMineAsync(CancellationToken cancellationToken = default);
    Task<ServiceResult<IReadOnlyCollection<BookingRequestResponse>>> GetOwnerRequestsAsync(CancellationToken cancellationToken = default);
    Task<ServiceResult<BookingRequestResponse>> ApproveAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ServiceResult<BookingRequestResponse>> RejectAsync(Guid id, string? reason = null, CancellationToken cancellationToken = default);
    Task<ServiceResult<BookingResponse>> CancelAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ServiceResult<BookingDetailResponse>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    // Owner marks the toy as handed over (Approved → Active).
    Task<ServiceResult<BookingDetailResponse>> MarkActiveAsync(Guid id, CancellationToken cancellationToken = default);

    // Owner marks the rental as completed (Active → Completed).
    Task<ServiceResult<BookingDetailResponse>> CompleteAsync(Guid id, CancellationToken cancellationToken = default);
}
