using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Services;

public sealed class ListingImagesOwnerService : IListingImagesOwnerService
{
    // Per-upload count cap. The HTTP layer also enforces a multipart body size
    // limit; this cap protects against many small files in one multipart batch.
    public const int MaxImagesPerUpload = 10;

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
        "image/gif"
    };

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".webp",
        ".gif"
    };

    private static class ErrorCodes
    {
        public const string Unauthenticated = "listing.unauthenticated";
        public const string UserBlocked = "listing.user_blocked";
        public const string ListingNotFound = "listing.not_found";
        public const string ListingForbidden = "listing.forbidden";
        public const string EmptyFile = "listing.image_empty";
        public const string InvalidFileType = "listing.image_invalid_type";
        public const string TooManyImages = "listing.image_too_many";
    }

    private readonly ICurrentUserContext _currentUserContext;
    private readonly IListingsOwnerStore _listingsOwnerStore;
    private readonly IFileStorageService _fileStorageService;

    public ListingImagesOwnerService(
        ICurrentUserContext currentUserContext,
        IListingsOwnerStore listingsOwnerStore,
        IFileStorageService fileStorageService)
    {
        _currentUserContext = currentUserContext;
        _listingsOwnerStore = listingsOwnerStore;
        _fileStorageService = fileStorageService;
    }

    public async Task<ServiceResult<IReadOnlyCollection<ListingImageResponse>>> UploadAsync(
        Guid listingId,
        IReadOnlyCollection<UploadListingImageRequest> files,
        CancellationToken cancellationToken = default)
    {
        if (_currentUserContext.UserId is not { } userId)
        {
            return ServiceResult<IReadOnlyCollection<ListingImageResponse>>.Failure(new ServiceError
            {
                Code = ErrorCodes.Unauthenticated,
                Message = "Current user is not authenticated."
            });
        }

        var user = await _listingsOwnerStore.FindUserByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return ServiceResult<IReadOnlyCollection<ListingImageResponse>>.Failure(new ServiceError
            {
                Code = ErrorCodes.Unauthenticated,
                Message = "Current user is not authenticated."
            });
        }

        if (user.IsBlocked)
        {
            return ServiceResult<IReadOnlyCollection<ListingImageResponse>>.Failure(new ServiceError
            {
                Code = ErrorCodes.UserBlocked,
                Message = "Blocked users cannot upload listing images."
            });
        }

        var listing = await _listingsOwnerStore.FindListingByIdWithImagesAsync(listingId, cancellationToken);
        if (listing is null)
        {
            return ServiceResult<IReadOnlyCollection<ListingImageResponse>>.Failure(new ServiceError
            {
                Code = ErrorCodes.ListingNotFound,
                Message = "Listing was not found."
            });
        }

        if (listing.OwnerId != userId)
        {
            return ServiceResult<IReadOnlyCollection<ListingImageResponse>>.Failure(new ServiceError
            {
                Code = ErrorCodes.ListingForbidden,
                Message = "Only the listing owner can upload images."
            });
        }

        if (files.Count == 0)
        {
            return ServiceResult<IReadOnlyCollection<ListingImageResponse>>.Failure(new ServiceError
            {
                Code = ErrorCodes.EmptyFile,
                Message = "At least one file must be provided."
            });
        }

        if (files.Count > MaxImagesPerUpload)
        {
            return ServiceResult<IReadOnlyCollection<ListingImageResponse>>.Failure(new ServiceError
            {
                Code = ErrorCodes.TooManyImages,
                Message = $"Up to {MaxImagesPerUpload} images can be uploaded per request."
            });
        }

        var uploadedImages = new List<ListingImage>();
        var hasPrimary = listing.Images.Any(image => image.IsPrimary);
        var sortOrder = listing.Images.Count == 0 ? 0 : listing.Images.Max(image => image.SortOrder) + 1;

        foreach (var file in files)
        {
            if (file.Length == 0)
            {
                return ServiceResult<IReadOnlyCollection<ListingImageResponse>>.Failure(new ServiceError
                {
                    Code = ErrorCodes.EmptyFile,
                    Message = $"File '{file.FileName}' is empty."
                });
            }

            var extension = Path.GetExtension(file.FileName);
            if (!AllowedContentTypes.Contains(file.ContentType) || !AllowedExtensions.Contains(extension))
            {
                return ServiceResult<IReadOnlyCollection<ListingImageResponse>>.Failure(new ServiceError
                {
                    Code = ErrorCodes.InvalidFileType,
                    Message = $"File '{file.FileName}' is not a supported image type."
                });
            }

            var storedUrl = await _fileStorageService.SaveListingImageAsync(
                file.Content,
                file.FileName,
                file.ContentType,
                cancellationToken);

            var image = new ListingImage
            {
                Id = Guid.NewGuid(),
                ListingId = listing.Id,
                Url = storedUrl,
                IsPrimary = !hasPrimary && uploadedImages.Count == 0,
                SortOrder = sortOrder++
            };

            uploadedImages.Add(image);
        }

        await _listingsOwnerStore.AddListingImagesAsync(uploadedImages, cancellationToken);
        listing.UpdatedAt = DateTime.UtcNow;
        await _listingsOwnerStore.SaveChangesAsync(cancellationToken);

        var response = uploadedImages
            .OrderBy(image => image.SortOrder)
            .Select(image => new ListingImageResponse
            {
                Id = image.Id,
                Url = image.Url,
                IsPrimary = image.IsPrimary,
                SortOrder = image.SortOrder
            })
            .ToList();

        return ServiceResult<IReadOnlyCollection<ListingImageResponse>>.Success(response);
    }
}
