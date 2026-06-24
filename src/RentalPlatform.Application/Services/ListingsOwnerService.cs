using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Services;

public sealed class ListingsOwnerService : IListingsOwnerService
{
    private static class ErrorCodes
    {
        public const string Unauthenticated = "listing.unauthenticated";
        public const string UserBlocked = "listing.user_blocked";
        public const string Forbidden = "listing.forbidden";
        public const string NotFound = "listing.not_found";
        public const string InvalidStatus = "listing.invalid_status";
        public const string CategoryNotFound = "listing.category_not_found";
        public const string InvalidAgeRange = "listing.invalid_age_range";
    }

    private readonly ICurrentUserContext _currentUserContext;
    private readonly IListingsOwnerStore _listingsOwnerStore;

    public ListingsOwnerService(ICurrentUserContext currentUserContext, IListingsOwnerStore listingsOwnerStore)
    {
        _currentUserContext = currentUserContext;
        _listingsOwnerStore = listingsOwnerStore;
    }

    public async Task<ServiceResult<CreateListingResponse>> CreateAsync(
        CreateListingRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_currentUserContext.UserId is not { } ownerId)
        {
            return ServiceResult<CreateListingResponse>.Failure(new ServiceError
            {
                Code = ErrorCodes.Unauthenticated,
                Message = "Current user is not authenticated."
            });
        }

        var user = await _listingsOwnerStore.FindUserByIdAsync(ownerId, cancellationToken);
        if (user is null)
        {
            return ServiceResult<CreateListingResponse>.Failure(new ServiceError
            {
                Code = ErrorCodes.Unauthenticated,
                Message = "Current user is not authenticated."
            });
        }

        if (user.IsBlocked)
        {
            return ServiceResult<CreateListingResponse>.Failure(new ServiceError
            {
                Code = ErrorCodes.UserBlocked,
                Message = "Blocked users cannot create listings."
            });
        }

        var categoryExists = await _listingsOwnerStore.CategoryExistsAsync(request.CategoryId, cancellationToken);
        if (!categoryExists)
        {
            return ServiceResult<CreateListingResponse>.Failure(new ServiceError
            {
                Code = ErrorCodes.CategoryNotFound,
                Message = "Category does not exist."
            });
        }

        if (request.AgeFromMonths is { } from &&
            request.AgeToMonths is { } to &&
            to < from)
        {
            return ServiceResult<CreateListingResponse>.Failure(new ServiceError
            {
                Code = ErrorCodes.InvalidAgeRange,
                Message = "Age (to) must be greater than or equal to age (from)."
            });
        }

        var now = DateTime.UtcNow;
        var listing = new Listing
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            CategoryId = request.CategoryId,
            Title = request.Title.Trim(),
            Description = request.Description.Trim(),
            PricePerDay = request.PricePerDay,
            Currency = string.IsNullOrWhiteSpace(request.Currency) ? "USD" : request.Currency.Trim().ToUpperInvariant(),
            Country = request.Country.Trim(),
            City = request.City.Trim(),
            AddressLine = NormalizeOptional(request.AddressLine),
            Latitude = request.Latitude,
            Longitude = request.Longitude,
            AgeFromMonths = request.AgeFromMonths,
            AgeToMonths = request.AgeToMonths,
            Condition = NormalizeOptional(request.Condition),
            HygieneNotes = NormalizeOptional(request.HygieneNotes),
            SafetyNotes = NormalizeOptional(request.SafetyNotes),
            DepositAmount = request.DepositAmount,
            Status = ListingStatus.PendingApproval,
            CreatedAt = now,
            UpdatedAt = now
        };

        await _listingsOwnerStore.AddListingAsync(listing, cancellationToken);
        await _listingsOwnerStore.SaveChangesAsync(cancellationToken);

        return ServiceResult<CreateListingResponse>.Success(new CreateListingResponse
        {
            Id = listing.Id,
            Status = listing.Status,
            CreatedAt = listing.CreatedAt,
            Message = "Toy listing submitted for review."
        });
    }

    public async Task<ServiceResult<IReadOnlyCollection<MyListingResponse>>> GetMineAsync(
        CancellationToken cancellationToken = default)
    {
        if (_currentUserContext.UserId is not { } ownerId)
        {
            return ServiceResult<IReadOnlyCollection<MyListingResponse>>.Failure(new ServiceError
            {
                Code = ErrorCodes.Unauthenticated,
                Message = "Current user is not authenticated."
            });
        }

        var user = await _listingsOwnerStore.FindUserByIdAsync(ownerId, cancellationToken);
        if (user is null)
        {
            return ServiceResult<IReadOnlyCollection<MyListingResponse>>.Failure(new ServiceError
            {
                Code = ErrorCodes.Unauthenticated,
                Message = "Current user is not authenticated."
            });
        }

        if (user.IsBlocked)
        {
            return ServiceResult<IReadOnlyCollection<MyListingResponse>>.Failure(new ServiceError
            {
                Code = ErrorCodes.UserBlocked,
                Message = "Blocked users cannot access owner listings."
            });
        }

        var listings = await _listingsOwnerStore.GetListingsByOwnerIdAsync(ownerId, cancellationToken);

        var response = listings
            .OrderByDescending(listing => listing.CreatedAt)
            .Select(listing => new MyListingResponse
            {
                Id = listing.Id,
                CategoryId = listing.CategoryId,
                CategoryName = listing.Category.Name,
                Title = listing.Title,
                Description = listing.Description,
                PricePerDay = listing.PricePerDay,
                Currency = listing.Currency,
                Country = listing.Country,
                City = listing.City,
                AgeFromMonths = listing.AgeFromMonths,
                AgeToMonths = listing.AgeToMonths,
                Condition = listing.Condition,
                HygieneNotes = listing.HygieneNotes,
                SafetyNotes = listing.SafetyNotes,
                DepositAmount = listing.DepositAmount,
                Status = listing.Status,
                RejectionReason = listing.RejectionReason,
                PrimaryImageUrl = listing.Images
                    .OrderByDescending(image => image.IsPrimary)
                    .ThenBy(image => image.SortOrder)
                    .Select(image => image.Url)
                    .FirstOrDefault(),
                ModeratedAt = listing.ModeratedAt,
                CreatedAt = listing.CreatedAt,
                UpdatedAt = listing.UpdatedAt
            })
            .ToList();

        return ServiceResult<IReadOnlyCollection<MyListingResponse>>.Success(response);
    }

    public async Task<ServiceResult<bool>> ArchiveAsync(
        Guid listingId,
        CancellationToken cancellationToken = default)
    {
        if (_currentUserContext.UserId is not { } ownerId)
        {
            return ServiceResult<bool>.Failure(new ServiceError
            {
                Code = ErrorCodes.Unauthenticated,
                Message = "Current user is not authenticated."
            });
        }

        var listing = await _listingsOwnerStore.FindListingByIdAndOwnerAsync(listingId, ownerId, cancellationToken);
        if (listing is null)
        {
            return ServiceResult<bool>.Failure(new ServiceError
            {
                Code = ErrorCodes.NotFound,
                Message = "Listing not found."
            });
        }

        if (listing.Status == ListingStatus.Archived)
        {
            return ServiceResult<bool>.Failure(new ServiceError
            {
                Code = ErrorCodes.InvalidStatus,
                Message = "Listing is already archived."
            });
        }

        listing.Status = ListingStatus.Archived;
        listing.UpdatedAt = DateTime.UtcNow;
        await _listingsOwnerStore.SaveChangesAsync(cancellationToken);

        return ServiceResult<bool>.Success(true);
    }

    public async Task<ServiceResult<bool>> RestoreAsync(
        Guid listingId,
        CancellationToken cancellationToken = default)
    {
        if (_currentUserContext.UserId is not { } ownerId)
        {
            return ServiceResult<bool>.Failure(new ServiceError
            {
                Code = ErrorCodes.Unauthenticated,
                Message = "Current user is not authenticated."
            });
        }

        var listing = await _listingsOwnerStore.FindListingByIdAndOwnerAsync(listingId, ownerId, cancellationToken);
        if (listing is null)
        {
            return ServiceResult<bool>.Failure(new ServiceError
            {
                Code = ErrorCodes.NotFound,
                Message = "Listing not found."
            });
        }

        if (listing.Status != ListingStatus.Archived)
        {
            return ServiceResult<bool>.Failure(new ServiceError
            {
                Code = ErrorCodes.InvalidStatus,
                Message = "Only archived listings can be restored."
            });
        }

        listing.Status = ListingStatus.PendingApproval;
        listing.UpdatedAt = DateTime.UtcNow;
        await _listingsOwnerStore.SaveChangesAsync(cancellationToken);

        return ServiceResult<bool>.Success(true);
    }

    public async Task<ServiceResult<Guid>> UpdateAsync(
        Guid listingId,
        UpdateListingRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_currentUserContext.UserId is not { } ownerId)
        {
            return ServiceResult<Guid>.Failure(new ServiceError
            {
                Code = ErrorCodes.Unauthenticated,
                Message = "Current user is not authenticated."
            });
        }

        var listing = await _listingsOwnerStore.FindListingByIdAndOwnerAsync(listingId, ownerId, cancellationToken);
        if (listing is null)
        {
            return ServiceResult<Guid>.Failure(new ServiceError
            {
                Code = ErrorCodes.NotFound,
                Message = "Listing not found."
            });
        }

        if (listing.Status == ListingStatus.Archived)
        {
            return ServiceResult<Guid>.Failure(new ServiceError
            {
                Code = ErrorCodes.InvalidStatus,
                Message = "Archived listings cannot be edited."
            });
        }

        if (request.AgeFromMonths is { } from && request.AgeToMonths is { } to && to < from)
        {
            return ServiceResult<Guid>.Failure(new ServiceError
            {
                Code = ErrorCodes.InvalidAgeRange,
                Message = "Age (to) must be greater than or equal to age (from)."
            });
        }

        // Track whether any publicly-visible free-text content changed. Such fields were vetted
        // during moderation, so altering them on an already-approved listing must re-trigger review
        // (otherwise an owner could get innocent text approved and then swap in disallowed content).
        // This mirrors the image-replace path, which always re-moderates.
        var contentChanged = false;

        if (request.Title is not null) contentChanged |= SetIfChanged(() => listing.Title, v => listing.Title = v!, request.Title.Trim());
        if (request.Description is not null) contentChanged |= SetIfChanged(() => listing.Description, v => listing.Description = v!, request.Description.Trim());
        if (request.Condition is not null) contentChanged |= SetIfChanged(() => listing.Condition, v => listing.Condition = v, NormalizeOptional(request.Condition));
        if (request.HygieneNotes is not null) contentChanged |= SetIfChanged(() => listing.HygieneNotes, v => listing.HygieneNotes = v, NormalizeOptional(request.HygieneNotes));
        if (request.SafetyNotes is not null) contentChanged |= SetIfChanged(() => listing.SafetyNotes, v => listing.SafetyNotes = v, NormalizeOptional(request.SafetyNotes));

        // Structured, non-content fields — never require re-moderation.
        if (request.PricePerDay is not null) listing.PricePerDay = request.PricePerDay.Value;
        if (request.City is not null) listing.City = request.City.Trim();
        if (request.Country is not null) listing.Country = request.Country.Trim();
        if (request.AgeFromMonths is not null) listing.AgeFromMonths = request.AgeFromMonths;
        if (request.AgeToMonths is not null) listing.AgeToMonths = request.AgeToMonths;
        if (request.DepositAmount is not null) listing.DepositAmount = request.DepositAmount;

        if (listing.Status == ListingStatus.Rejected)
        {
            listing.Status = ListingStatus.PendingApproval;
            listing.RejectionReason = null;
        }
        else if (listing.Status == ListingStatus.Approved && contentChanged)
        {
            listing.Status = ListingStatus.PendingApproval;
        }

        listing.UpdatedAt = DateTime.UtcNow;
        await _listingsOwnerStore.SaveChangesAsync(cancellationToken);

        return ServiceResult<Guid>.Success(listing.Id);
    }

    // Applies a new value and reports whether it actually differed from the current one.
    private static bool SetIfChanged(Func<string?> getter, Action<string?> setter, string? newValue)
    {
        if (string.Equals(getter(), newValue, StringComparison.Ordinal))
        {
            return false;
        }

        setter(newValue);
        return true;
    }

    private static string? NormalizeOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }
}
