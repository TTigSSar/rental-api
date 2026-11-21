using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface IAvatarService
{
    /// <summary>Null content = the request did not carry exactly one file (auth.avatar_missing).</summary>
    Task<ServiceResult<CurrentUserResponse>> UploadAsync(AvatarContent? content, CancellationToken cancellationToken = default);

    Task<ServiceResult<CurrentUserResponse>> RemoveAsync(CancellationToken cancellationToken = default);
}
