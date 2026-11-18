using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface IPushSubscriptionService
{
    ServiceResult<PushPublicKeyResponse> GetPublicKey();
    Task<ServiceResult<bool>> SubscribeAsync(PushSubscriptionRequest request, CancellationToken cancellationToken = default);
    Task<ServiceResult<bool>> UnsubscribeAsync(PushUnsubscribeRequest request, CancellationToken cancellationToken = default);
}
