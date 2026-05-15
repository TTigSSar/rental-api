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
    private readonly IEmailService _emailService;

    public AdminListingsService(
        ICurrentUserContext currentUserContext,
        IAdminListingsStore adminListingsStore,
        IEmailService emailService)
    {
        _currentUserContext = currentUserContext;
        _adminListingsStore = adminListingsStore;
        _emailService = emailService;
    }

    public async Task<ServiceResult<IReadOnlyCollection<PendingListingForReviewResponse>>> GetPendingAsync(
        CancellationToken cancellationToken = default)
    {
        var adminResult = await EnsureAdminAsync(cancellationToken);
        if (!adminResult.IsSuccess)
        {
            return ServiceResult<IReadOnlyCollection<PendingListingForReviewResponse>>.Failure(adminResult.Error!);
        }

        var listings = await _adminListingsStore.GetPendingListingsAsync(cancellationToken);
        var response = listings.Select(MapToPendingReview).ToList();

        return ServiceResult<IReadOnlyCollection<PendingListingForReviewResponse>>.Success(response);
    }

    public async Task<ServiceResult<ModerateListingResponse>> ApproveAsync(
        Guid listingId,
        CancellationToken cancellationToken = default)
    {
        var adminResult = await EnsureAdminAsync(cancellationToken);
        if (!adminResult.IsSuccess)
        {
            return ServiceResult<ModerateListingResponse>.Failure(adminResult.Error!);
        }

        var admin = adminResult.Value!;

        var listing = await _adminListingsStore.FindListingByIdAsync(listingId, cancellationToken);
        if (listing is null)
        {
            return Failure<ModerateListingResponse>(ErrorCodes.ListingNotFound, "Listing was not found.");
        }

        if (listing.Status != ListingStatus.PendingApproval)
        {
            return Failure<ModerateListingResponse>(ErrorCodes.InvalidStatus, "Only pending listings can be moderated.");
        }

        var now = DateTime.UtcNow;
        listing.Status = ListingStatus.Approved;
        listing.RejectionReason = null;
        listing.ModeratedAt = now;
        listing.ModeratedByUserId = admin.Id;
        listing.UpdatedAt = now;

        await _adminListingsStore.SaveChangesAsync(cancellationToken);

        // IEmailService contract: never throws. Moderation success is not conditional on delivery.
        await _emailService.SendListingApprovedAsync(
            listing.Owner.Email,
            $"{listing.Owner.FirstName} {listing.Owner.LastName}".Trim(),
            listing.Title,
            cancellationToken);

        return ServiceResult<ModerateListingResponse>.Success(new ModerateListingResponse
        {
            Id = listing.Id,
            Status = listing.Status,
            RejectionReason = null,
            ModeratedAt = now,
            Message = "Listing approved and is now publicly visible."
        });
    }

    public async Task<ServiceResult<ModerateListingResponse>> RejectAsync(
        Guid listingId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var adminResult = await EnsureAdminAsync(cancellationToken);
        if (!adminResult.IsSuccess)
        {
            return ServiceResult<ModerateListingResponse>.Failure(adminResult.Error!);
        }

        var admin = adminResult.Value!;

        var listing = await _adminListingsStore.FindListingByIdAsync(listingId, cancellationToken);
        if (listing is null)
        {
            return Failure<ModerateListingResponse>(ErrorCodes.ListingNotFound, "Listing was not found.");
        }

        if (listing.Status != ListingStatus.PendingApproval)
        {
            return Failure<ModerateListingResponse>(ErrorCodes.InvalidStatus, "Only pending listings can be moderated.");
        }

        var trimmedReason = reason.Trim();
        var now = DateTime.UtcNow;
        listing.Status = ListingStatus.Rejected;
        listing.RejectionReason = trimmedReason;
        listing.ModeratedAt = now;
        listing.ModeratedByUserId = admin.Id;
        listing.UpdatedAt = now;

        await _adminListingsStore.SaveChangesAsync(cancellationToken);

        // IEmailService contract: never throws. Moderation success is not conditional on delivery.
        await _emailService.SendListingRejectedAsync(
            listing.Owner.Email,
            $"{listing.Owner.FirstName} {listing.Owner.LastName}".Trim(),
            listing.Title,
            trimmedReason,
            cancellationToken);

        return ServiceResult<ModerateListingResponse>.Success(new ModerateListingResponse
        {
            Id = listing.Id,
            Status = listing.Status,
            RejectionReason = trimmedReason,
            ModeratedAt = now,
            Message = "Listing rejected and owner has been notified."
        });
    }

    // Returns the authenticated admin User or a failure result.
    private async Task<ServiceResult<User>> EnsureAdminAsync(CancellationToken cancellationToken)
    {
        if (_currentUserContext.UserId is not { } userId)
        {
            return Failure<User>(ErrorCodes.Unauthenticated, "Current user is not authenticated.");
        }

        var user = await _adminListingsStore.FindUserByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return Failure<User>(ErrorCodes.Unauthenticated, "Current user is not authenticated.");
        }

        if (user.Role != UserRole.Admin || user.IsBlocked)
        {
            return Failure<User>(ErrorCodes.Forbidden, "Admin privileges are required.");
        }

        return ServiceResult<User>.Success(user);
    }

    private static PendingListingForReviewResponse MapToPendingReview(Listing listing) => new()
    {
        Id = listing.Id,
        OwnerId = listing.OwnerId,
        OwnerEmail = listing.Owner.Email,
        OwnerFirstName = listing.Owner.FirstName,
        OwnerLastName = listing.Owner.LastName,
        CategoryId = listing.CategoryId,
        CategoryName = listing.Category.Name,
        Title = listing.Title,
        Description = listing.Description,
        PricePerDay = listing.PricePerDay,
        Currency = listing.Currency,
        Country = listing.Country,
        City = listing.City,
        AddressLine = listing.AddressLine,
        AgeFromMonths = listing.AgeFromMonths,
        AgeToMonths = listing.AgeToMonths,
        Condition = listing.Condition,
        HygieneNotes = listing.HygieneNotes,
        SafetyNotes = listing.SafetyNotes,
        DepositAmount = listing.DepositAmount,
        Images = listing.Images
            .OrderByDescending(img => img.IsPrimary)
            .ThenBy(img => img.SortOrder)
            .Select(img => new ListingImageResponse
            {
                Id = img.Id,
                Url = img.Url,
                IsPrimary = img.IsPrimary,
                SortOrder = img.SortOrder
            })
            .ToList(),
        CreatedAt = listing.CreatedAt
    };

    private static ServiceResult<T> Failure<T>(string code, string message) =>
        ServiceResult<T>.Failure(new ServiceError { Code = code, Message = message });
}
