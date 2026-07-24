using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

public interface IListingsQueryService
{
    Task<PagedResult<ListingPreviewResponse>> GetApprovedListingsAsync(
        ListingsQueryFilter filter,
        CancellationToken cancellationToken = default);

    // Maps P2-1: flat, capped list of map pins for the same filter chain as
    // GetApprovedListingsAsync — reuses the predicate, differs only in projection/shape.
    Task<ListingMapPinsResponse> GetMapPinsAsync(
        ListingsQueryFilter filter,
        CancellationToken cancellationToken = default);

    Task<ListingDetailsResponse?> GetApprovedListingByIdAsync(
        Guid id,
        Guid? callerId = null,
        bool isAdmin = false,
        CancellationToken cancellationToken = default);
}
