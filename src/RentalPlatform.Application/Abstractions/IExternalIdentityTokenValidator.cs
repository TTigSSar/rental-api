using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface IExternalIdentityTokenValidator
{
    Task<ServiceResult<ExternalUserInfo>> ValidateAsync(
        string provider,
        string idToken,
        CancellationToken cancellationToken = default);
}
