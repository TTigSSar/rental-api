using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface IBookingsService
{
    Task<ServiceResult<BookingResponse>> CreateAsync(CreateBookingRequest request, CancellationToken cancellationToken = default);
    Task<ServiceResult<IReadOnlyCollection<BookingResponse>>> GetMineAsync(CancellationToken cancellationToken = default);
    Task<ServiceResult<IReadOnlyCollection<BookingRequestResponse>>> GetOwnerRequestsAsync(CancellationToken cancellationToken = default);
    Task<ServiceResult<BookingRequestResponse>> ApproveAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ServiceResult<BookingRequestResponse>> RejectAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ServiceResult<BookingResponse>> CancelAsync(Guid id, CancellationToken cancellationToken = default);
}
