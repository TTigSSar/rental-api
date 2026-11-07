using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface IPublicUserProfileService
{
    /// <summary>Returns the public profile for <paramref name="userId"/>, or null if the user does not exist or is deleted (ADR-056).</summary>
    Task<PublicUserProfileResponse?> GetPublicProfileAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Returns all approved listings owned by <paramref name="userId"/>; null for a deleted user (ADR-056).</summary>
    Task<IReadOnlyList<ListingPreviewResponse>?> GetUserListingsAsync(Guid userId, CancellationToken cancellationToken = default);
}
