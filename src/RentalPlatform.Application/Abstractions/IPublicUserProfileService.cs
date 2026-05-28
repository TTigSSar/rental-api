using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface IPublicUserProfileService
{
    /// <summary>Returns the public profile for <paramref name="userId"/>, or null if the user does not exist.</summary>
    Task<PublicUserProfileResponse?> GetPublicProfileAsync(Guid userId, CancellationToken cancellationToken = default);
}
