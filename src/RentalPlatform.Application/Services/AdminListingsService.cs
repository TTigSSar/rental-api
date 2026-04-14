using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Services;

public sealed class AdminListingsService : IAdminListingsService
{
    private static class ErrorCodes
    {
        public const string Unauthenticated = "admin.unauthenticated";
        public const string Forbidden = "admin.forbidden";
        public const string ListingNotFound = "admin.listing_not_found";
        public const string InvalidStatus = "admin.invalid_listing_status";
    }

    private readonly ICurrentUserContext _currentUserContext;
    private readonly IAdminListingsStore _adminListingsStore;

    public AdminListingsService(ICurrentUserContext currentUserContext, IAdminListingsStore adminListingsStore)
    {
        _currentUserContext = currentUserContext;
        _adminListingsStore = adminListingsStore;
    }

    public async Task<ServiceResult<IReadOnlyCollection<AdminPendingListingResponse>>> GetPendingAsync(
        CancellationToken cancellationToken = default)
    {
        var adminCheck = await EnsureAdminAsync(cancellationToken);
        if (!adminCheck.IsSuccess)
        {
            return ServiceResult<IReadOnlyCollection<AdminPendingListingResponse>>.Failure(adminCheck.Error!);
        }

        var listings = await _adminListingsStore.GetPendingListingsAsync(cancellationToken);
        var response = listings
            .OrderBy(listing => listing.CreatedAt)
            .Select(Map)
            .ToList();

        return ServiceResult<IReadOnlyCollection<AdminPendingListingResponse>>.Success(response);
    }

    public Task<ServiceResult<AdminPendingListingResponse>> ApproveAsync(Guid listingId, CancellationToken cancellationToken = default) =>
        UpdateStatusAsync(listingId, ListingStatus.Approved, cancellationToken);

    public Task<ServiceResult<AdminPendingListingResponse>> RejectAsync(Guid listingId, CancellationToken cancellationToken = default) =>
        UpdateStatusAsync(listingId, ListingStatus.Rejected, cancellationToken);

    private async Task<ServiceResult<AdminPendingListingResponse>> UpdateStatusAsync(
        Guid listingId,
        ListingStatus targetStatus,
        CancellationToken cancellationToken)
    {
        var adminCheck = await EnsureAdminAsync(cancellationToken);
        if (!adminCheck.IsSuccess)
        {
            return ServiceResult<AdminPendingListingResponse>.Failure(adminCheck.Error!);
        }

        var listing = await _adminListingsStore.FindListingByIdAsync(listingId, cancellationToken);
        if (listing is null)
        {
            return Failure<AdminPendingListingResponse>(ErrorCodes.ListingNotFound, "Listing was not found.");
        }

        if (listing.Status != ListingStatus.PendingApproval)
        {
            return Failure<AdminPendingListingResponse>(ErrorCodes.InvalidStatus, "Only pending listings can be moderated.");
        }

        listing.Status = targetStatus;
        listing.UpdatedAt = DateTime.UtcNow;
        await _adminListingsStore.SaveChangesAsync(cancellationToken);

        return ServiceResult<AdminPendingListingResponse>.Success(Map(listing));
    }

    private async Task<ServiceResult<bool>> EnsureAdminAsync(CancellationToken cancellationToken)
    {
        if (_currentUserContext.UserId is not { } userId)
        {
            return Failure<bool>(ErrorCodes.Unauthenticated, "Current user is not authenticated.");
        }

        var user = await _adminListingsStore.FindUserByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return Failure<bool>(ErrorCodes.Unauthenticated, "Current user is not authenticated.");
        }

        if (user.Role != UserRole.Admin || user.IsBlocked)
        {
            return Failure<bool>(ErrorCodes.Forbidden, "Admin privileges are required.");
        }

        return ServiceResult<bool>.Success(true);
    }

    private static AdminPendingListingResponse Map(Listing listing) => new()
    {
        Id = listing.Id,
        OwnerId = listing.OwnerId,
        OwnerEmail = listing.Owner.Email,
        CategoryId = listing.CategoryId,
        CategoryName = listing.Category.Name,
        Title = listing.Title,
        City = listing.City,
        Country = listing.Country,
        Status = listing.Status,
        CreatedAt = listing.CreatedAt,
        UpdatedAt = listing.UpdatedAt
    };

    private static ServiceResult<T> Failure<T>(string code, string message) =>
        ServiceResult<T>.Failure(new ServiceError
        {
            Code = code,
            Message = message
        });
}
