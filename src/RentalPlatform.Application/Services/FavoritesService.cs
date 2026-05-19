using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Services;

public sealed class FavoritesService : IFavoritesService
{
    private static class ErrorCodes
    {
        public const string Unauthenticated = "favorite.unauthenticated";
        public const string UserBlocked = "favorite.user_blocked";
        public const string ListingNotFound = "favorite.listing_not_found";
    }

    private readonly ICurrentUserContext _currentUserContext;
    private readonly IFavoritesStore _favoritesStore;

    public FavoritesService(ICurrentUserContext currentUserContext, IFavoritesStore favoritesStore)
    {
        _currentUserContext = currentUserContext;
        _favoritesStore = favoritesStore;
    }

    public async Task<ServiceResult<IReadOnlyCollection<ListingPreviewResponse>>> GetMineAsync(CancellationToken cancellationToken = default)
    {
        var userResult = await GetCurrentUserAsync(cancellationToken);
        if (!userResult.IsSuccess || userResult.Value is null)
        {
            return ServiceResult<IReadOnlyCollection<ListingPreviewResponse>>.Failure(userResult.Error!);
        }

        var favorites = await _favoritesStore.GetByUserIdAsync(userResult.Value.Id, cancellationToken);
        var response = favorites
            .OrderByDescending(favorite => favorite.CreatedAt)
            .Select(favorite => new ListingPreviewResponse
            {
                Id = favorite.Listing.Id,
                CategoryId = favorite.Listing.CategoryId,
                CategoryName = favorite.Listing.Category.Name,
                Title = favorite.Listing.Title,
                PricePerDay = favorite.Listing.PricePerDay,
                Currency = favorite.Listing.Currency,
                Country = favorite.Listing.Country,
                City = favorite.Listing.City,
                PrimaryImageUrl = favorite.Listing.Images
                    .OrderByDescending(image => image.IsPrimary)
                    .ThenBy(image => image.SortOrder)
                    .Select(image => image.Url)
                    .FirstOrDefault(),
                AgeFromMonths = favorite.Listing.AgeFromMonths,
                AgeToMonths = favorite.Listing.AgeToMonths,
                Condition = favorite.Listing.Condition,
                CreatedAt = favorite.Listing.CreatedAt
            })
            .ToList();

        return ServiceResult<IReadOnlyCollection<ListingPreviewResponse>>.Success(response);
    }

    public async Task<ServiceResult<bool>> AddAsync(Guid listingId, CancellationToken cancellationToken = default)
    {
        var userResult = await GetCurrentUserAsync(cancellationToken);
        if (!userResult.IsSuccess || userResult.Value is null)
        {
            return ServiceResult<bool>.Failure(userResult.Error!);
        }

        var listing = await _favoritesStore.FindListingByIdAsync(listingId, cancellationToken);
        if (listing is null)
        {
            return Failure<bool>(ErrorCodes.ListingNotFound, "Listing was not found.");
        }

        var existing = await _favoritesStore.FindByUserAndListingAsync(userResult.Value.Id, listingId, cancellationToken);
        if (existing is not null)
        {
            return ServiceResult<bool>.Success(false);
        }

        var added = await _favoritesStore.TryAddAsync(new Favorite
        {
            Id = Guid.NewGuid(),
            UserId = userResult.Value.Id,
            ListingId = listingId,
            CreatedAt = DateTime.UtcNow
        }, cancellationToken);

        return ServiceResult<bool>.Success(added);
    }

    public async Task<ServiceResult<bool>> RemoveAsync(Guid listingId, CancellationToken cancellationToken = default)
    {
        var userResult = await GetCurrentUserAsync(cancellationToken);
        if (!userResult.IsSuccess || userResult.Value is null)
        {
            return ServiceResult<bool>.Failure(userResult.Error!);
        }

        var existing = await _favoritesStore.FindByUserAndListingAsync(userResult.Value.Id, listingId, cancellationToken);
        if (existing is null)
        {
            return ServiceResult<bool>.Success(false);
        }

        _favoritesStore.Remove(existing);
        await _favoritesStore.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Success(true);
    }

    private async Task<ServiceResult<User>> GetCurrentUserAsync(CancellationToken cancellationToken)
    {
        if (_currentUserContext.UserId is not { } userId)
        {
            return Failure<User>(ErrorCodes.Unauthenticated, "Current user is not authenticated.");
        }

        var user = await _favoritesStore.FindUserByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return Failure<User>(ErrorCodes.Unauthenticated, "Current user is not authenticated.");
        }

        if (user.IsBlocked)
        {
            return Failure<User>(ErrorCodes.UserBlocked, "Blocked users cannot manage favorites.");
        }

        return ServiceResult<User>.Success(user);
    }

    private static ServiceResult<T> Failure<T>(string code, string message) =>
        ServiceResult<T>.Failure(new ServiceError
        {
            Code = code,
            Message = message
        });
}
