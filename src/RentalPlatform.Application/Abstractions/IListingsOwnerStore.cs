using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Abstractions;

public interface IListingsOwnerStore
{
    Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<bool> CategoryExistsAsync(Guid categoryId, CancellationToken cancellationToken = default);
    Task<bool> DistrictExistsAsync(Guid districtId, CancellationToken cancellationToken = default);
    Task<Guid?> FindDistrictIdByCodeAsync(string code, CancellationToken cancellationToken = default);
    Task AddListingAsync(Listing listing, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<Listing>> GetListingsByOwnerIdAsync(Guid ownerId, CancellationToken cancellationToken = default);
    Task<Listing?> FindListingByIdWithImagesAsync(Guid listingId, CancellationToken cancellationToken = default);
    Task<Listing?> FindListingByIdAndOwnerAsync(Guid listingId, Guid ownerId, CancellationToken cancellationToken = default);
    Task AddListingImagesAsync(IEnumerable<ListingImage> images, CancellationToken cancellationToken = default);
    void RemoveListingImage(ListingImage image);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
