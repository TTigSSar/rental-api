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
            Currency = request.Currency.Trim().ToUpperInvariant(),
            Country = request.Country.Trim(),
            City = request.City.Trim(),
            AddressLine = request.AddressLine.Trim(),
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
            CreatedAt = listing.CreatedAt
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
                PricePerDay = listing.PricePerDay,
                Currency = listing.Currency,
                Country = listing.Country,
                City = listing.City,
                Status = listing.Status,
                CreatedAt = listing.CreatedAt,
                UpdatedAt = listing.UpdatedAt
            })
            .ToList();

        return ServiceResult<IReadOnlyCollection<MyListingResponse>>.Success(response);
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
