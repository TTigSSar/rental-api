using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Services;

public sealed class ListingImagesOwnerService : IListingImagesOwnerService
{
    // Per-upload count cap. The HTTP layer also enforces a multipart body size
    // limit; this cap protects against many small files in one multipart batch.
    public const int MaxImagesPerUpload = 10;

    // Hard ceiling on how many images a single listing can accumulate over time.
    public const int MaxImagesPerListing = 20;

    // Per-file size ceiling. The HTTP layer caps the whole multipart body; this
    // bounds each individual image so one huge file cannot consume the budget.
    public const long MaxBytesPerFile = 5L * 1024 * 1024;

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
        public const string ListingImageLimit = "listing.image_listing_limit";
        public const string FileTooLarge = "listing.image_too_large";
        public const string ImageNotFound = "listing.image_not_found";
        public const string InvalidReorder = "listing.image_invalid_reorder";
        public const string ArchivedListing = "listing.invalid_status";
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
        var ownerResult = await ResolveOwnedListingAsync(listingId, cancellationToken);
        if (!ownerResult.IsSuccess || ownerResult.Value is null)
        {
            return Failure(ownerResult.Error!);
        }

        var listing = ownerResult.Value;

        if (files.Count == 0)
        {
            return Failure(ErrorCodes.EmptyFile, "At least one file must be provided.");
        }

        if (files.Count > MaxImagesPerUpload)
        {
            return Failure(ErrorCodes.TooManyImages, $"Up to {MaxImagesPerUpload} images can be uploaded per request.");
        }

        if (listing.Images.Count + files.Count > MaxImagesPerListing)
        {
            return Failure(
                ErrorCodes.ListingImageLimit,
                $"A listing can have at most {MaxImagesPerListing} images. This listing already has {listing.Images.Count}.");
        }

        // Pass 1: buffer + fully validate every file BEFORE any disk write, so a late
        // validation failure cannot leave a partially-uploaded set of orphan files.
        var buffered = new List<BufferedUpload>(files.Count);
        try
        {
            foreach (var file in files)
            {
                if (file.Length > MaxBytesPerFile)
                {
                    return Failure(ErrorCodes.FileTooLarge, $"File '{file.FileName}' exceeds the {MaxBytesPerFile / (1024 * 1024)} MB limit.");
                }

                var extension = Path.GetExtension(file.FileName);
                if (string.IsNullOrEmpty(extension) || !AllowedExtensions.Contains(extension))
                {
                    return Failure(ErrorCodes.InvalidFileType, $"File '{file.FileName}' is not a supported image type.");
                }

                if (!AllowedContentTypes.Contains(file.ContentType))
                {
                    return Failure(ErrorCodes.InvalidFileType, $"File '{file.FileName}' is not a supported image type.");
                }

                var memory = new MemoryStream();
                buffered.Add(new BufferedUpload(memory, file.FileName, file.ContentType));
                await file.Content.CopyToAsync(memory, cancellationToken);

                if (memory.Length == 0)
                {
                    return Failure(ErrorCodes.EmptyFile, $"File '{file.FileName}' is empty.");
                }

                if (memory.Length > MaxBytesPerFile)
                {
                    return Failure(ErrorCodes.FileTooLarge, $"File '{file.FileName}' exceeds the {MaxBytesPerFile / (1024 * 1024)} MB limit.");
                }

                // Magic-byte check: the bytes themselves must look like a whitelisted image,
                // regardless of what the filename extension or content-type header claim.
                var header = new byte[ImageContentValidator.HeaderBytesRequired];
                memory.Position = 0;
                var read = await memory.ReadAsync(header, cancellationToken);
                memory.Position = 0;

                if (!ImageContentValidator.TryDetectMimeType(header.AsSpan(0, read), out var detectedMime)
                    || !AllowedContentTypes.Contains(detectedMime))
                {
                    return Failure(ErrorCodes.InvalidFileType, $"File '{file.FileName}' is not a valid or supported image.");
                }
            }

            // Pass 2: persist. Validation has fully passed for every file at this point.
            var uploadedImages = new List<ListingImage>(buffered.Count);
            var hasPrimary = listing.Images.Any(image => image.IsPrimary);
            var sortOrder = listing.Images.Count == 0 ? 0 : listing.Images.Max(image => image.SortOrder) + 1;

            foreach (var item in buffered)
            {
                item.Content.Position = 0;
                var storedUrl = await _fileStorageService.SaveListingImageAsync(
                    item.Content,
                    item.FileName,
                    item.ContentType,
                    listing.Id,
                    cancellationToken);

                uploadedImages.Add(new ListingImage
                {
                    Id = Guid.NewGuid(),
                    ListingId = listing.Id,
                    Url = storedUrl,
                    IsPrimary = !hasPrimary && uploadedImages.Count == 0,
                    SortOrder = sortOrder++
                });
            }

            await _listingsOwnerStore.AddListingImagesAsync(uploadedImages, cancellationToken);
            listing.UpdatedAt = DateTime.UtcNow;
            await _listingsOwnerStore.SaveChangesAsync(cancellationToken);

            return ServiceResult<IReadOnlyCollection<ListingImageResponse>>.Success(
                uploadedImages
                    .OrderBy(image => image.SortOrder)
                    .Select(ToResponse)
                    .ToList());
        }
        finally
        {
            foreach (var item in buffered)
            {
                await item.Content.DisposeAsync();
            }
        }
    }

    public async Task<ServiceResult<IReadOnlyCollection<ListingImageResponse>>> DeleteAsync(
        Guid listingId,
        Guid imageId,
        CancellationToken cancellationToken = default)
    {
        var ownerResult = await ResolveOwnedListingAsync(listingId, cancellationToken);
        if (!ownerResult.IsSuccess || ownerResult.Value is null)
        {
            return Failure(ownerResult.Error!);
        }

        var listing = ownerResult.Value;

        var image = listing.Images.FirstOrDefault(candidate => candidate.Id == imageId);
        if (image is null)
        {
            return Failure(ErrorCodes.ImageNotFound, "Image was not found on this listing.");
        }

        _listingsOwnerStore.RemoveListingImage(image);

        // Deterministic primary fallback: if the removed image was primary, promote the
        // remaining image with the lowest SortOrder so the listing always has a primary
        // while any images exist.
        if (image.IsPrimary)
        {
            var nextPrimary = listing.Images
                .Where(candidate => candidate.Id != imageId)
                .OrderBy(candidate => candidate.SortOrder)
                .ThenBy(candidate => candidate.Id)
                .FirstOrDefault();

            if (nextPrimary is not null)
            {
                nextPrimary.IsPrimary = true;
            }
        }

        listing.UpdatedAt = DateTime.UtcNow;
        await _listingsOwnerStore.SaveChangesAsync(cancellationToken);

        // Best-effort disk cleanup AFTER the row is gone. DeleteListingImageAsync is a
        // no-op for remote (seed) URLs and for already-missing files, so it never throws.
        await _fileStorageService.DeleteListingImageAsync(image.Url, cancellationToken);

        var remaining = listing.Images
            .Where(candidate => candidate.Id != imageId)
            .OrderByDescending(candidate => candidate.IsPrimary)
            .ThenBy(candidate => candidate.SortOrder)
            .ThenBy(candidate => candidate.Id)
            .Select(ToResponse)
            .ToList();

        return ServiceResult<IReadOnlyCollection<ListingImageResponse>>.Success(remaining);
    }

    public async Task<ServiceResult<IReadOnlyCollection<ListingImageResponse>>> ReplaceAsync(
        Guid listingId,
        IReadOnlyCollection<UploadListingImageRequest> files,
        CancellationToken cancellationToken = default)
    {
        var ownerResult = await ResolveOwnedListingAsync(listingId, cancellationToken);
        if (!ownerResult.IsSuccess || ownerResult.Value is null)
        {
            return Failure(ownerResult.Error!);
        }

        var listing = ownerResult.Value;

        if (files.Count == 0)
        {
            return Failure(ErrorCodes.EmptyFile, "At least one file must be provided.");
        }

        if (files.Count > MaxImagesPerUpload)
        {
            return Failure(ErrorCodes.TooManyImages, $"Up to {MaxImagesPerUpload} images can be uploaded per request.");
        }

        // Pass 1: buffer + validate every incoming file BEFORE touching the DB or disk,
        // so a late failure cannot leave the listing in a partially-replaced state.
        var buffered = new List<BufferedUpload>(files.Count);
        try
        {
            foreach (var file in files)
            {
                if (file.Length > MaxBytesPerFile)
                {
                    return Failure(ErrorCodes.FileTooLarge, $"File '{file.FileName}' exceeds the {MaxBytesPerFile / (1024 * 1024)} MB limit.");
                }

                var extension = Path.GetExtension(file.FileName);
                if (string.IsNullOrEmpty(extension) || !AllowedExtensions.Contains(extension))
                {
                    return Failure(ErrorCodes.InvalidFileType, $"File '{file.FileName}' is not a supported image type.");
                }

                if (!AllowedContentTypes.Contains(file.ContentType))
                {
                    return Failure(ErrorCodes.InvalidFileType, $"File '{file.FileName}' is not a supported image type.");
                }

                var memory = new MemoryStream();
                buffered.Add(new BufferedUpload(memory, file.FileName, file.ContentType));
                await file.Content.CopyToAsync(memory, cancellationToken);

                if (memory.Length == 0)
                {
                    return Failure(ErrorCodes.EmptyFile, $"File '{file.FileName}' is empty.");
                }

                if (memory.Length > MaxBytesPerFile)
                {
                    return Failure(ErrorCodes.FileTooLarge, $"File '{file.FileName}' exceeds the {MaxBytesPerFile / (1024 * 1024)} MB limit.");
                }

                var header = new byte[ImageContentValidator.HeaderBytesRequired];
                memory.Position = 0;
                var read = await memory.ReadAsync(header, cancellationToken);
                memory.Position = 0;

                if (!ImageContentValidator.TryDetectMimeType(header.AsSpan(0, read), out var detectedMime)
                    || !AllowedContentTypes.Contains(detectedMime))
                {
                    return Failure(ErrorCodes.InvalidFileType, $"File '{file.FileName}' is not a valid or supported image.");
                }
            }

            // Capture existing image URLs for best-effort physical cleanup after DB commit.
            var oldUrls = listing.Images.Select(img => img.Url).ToList();

            // Pass 2: remove all current DB rows. ToList() snapshots the collection so
            // we iterate a stable copy while the tracked collection is modified.
            foreach (var existing in listing.Images.ToList())
            {
                _listingsOwnerStore.RemoveListingImage(existing);
            }

            // Pass 3: persist new images starting at SortOrder 0; first is primary.
            var newImages = new List<ListingImage>(buffered.Count);
            var sortOrder = 0;
            foreach (var item in buffered)
            {
                item.Content.Position = 0;
                var storedUrl = await _fileStorageService.SaveListingImageAsync(
                    item.Content, item.FileName, item.ContentType, listing.Id, cancellationToken);

                newImages.Add(new ListingImage
                {
                    Id = Guid.NewGuid(),
                    ListingId = listing.Id,
                    Url = storedUrl,
                    IsPrimary = sortOrder == 0,
                    SortOrder = sortOrder++
                });
            }

            await _listingsOwnerStore.AddListingImagesAsync(newImages, cancellationToken);

            // Images are public-facing and were checked during original moderation.
            // Replacing them always triggers a fresh moderation cycle.
            listing.Status = ListingStatus.PendingApproval;
            listing.UpdatedAt = DateTime.UtcNow;
            await _listingsOwnerStore.SaveChangesAsync(cancellationToken);

            // Best-effort physical cleanup AFTER the DB commit. Mirrors the pattern in
            // DeleteAsync: never throws, no-ops for external/seed URLs, safe to skip.
            foreach (var url in oldUrls)
            {
                await _fileStorageService.DeleteListingImageAsync(url, cancellationToken);
            }

            return ServiceResult<IReadOnlyCollection<ListingImageResponse>>.Success(
                newImages.Select(ToResponse).ToList());
        }
        finally
        {
            foreach (var item in buffered)
            {
                await item.Content.DisposeAsync();
            }
        }
    }

    public async Task<ServiceResult<IReadOnlyCollection<ListingImageResponse>>> ReorderAsync(
        Guid listingId,
        ReorderListingImagesRequest request,
        CancellationToken cancellationToken = default)
    {
        var ownerResult = await ResolveOwnedListingAsync(listingId, cancellationToken);
        if (!ownerResult.IsSuccess || ownerResult.Value is null)
        {
            return Failure(ownerResult.Error!);
        }

        var listing = ownerResult.Value;

        var currentIds = listing.Images.Select(img => img.Id).ToHashSet();
        var suppliedIds = request.ImageIds.ToHashSet();

        if (!currentIds.SetEquals(suppliedIds))
        {
            return Failure(ErrorCodes.InvalidReorder, "The supplied image IDs must match exactly the current images of the listing.");
        }

        var imageById = listing.Images.ToDictionary(img => img.Id);
        for (var i = 0; i < request.ImageIds.Count; i++)
        {
            var image = imageById[request.ImageIds[i]];
            image.SortOrder = i;
            image.IsPrimary = i == 0;
        }

        listing.UpdatedAt = DateTime.UtcNow;
        await _listingsOwnerStore.SaveChangesAsync(cancellationToken);

        var ordered = request.ImageIds
            .Select((id, i) => ToResponse(imageById[id]))
            .ToList();

        return ServiceResult<IReadOnlyCollection<ListingImageResponse>>.Success(ordered);
    }

    // Resolves the current user, verifies they are an active owner of the listing,
    // and returns the tracked listing with its Images collection loaded.
    private async Task<ServiceResult<Listing>> ResolveOwnedListingAsync(
        Guid listingId,
        CancellationToken cancellationToken)
    {
        if (_currentUserContext.UserId is not { } userId)
        {
            return ServiceResult<Listing>.Failure(new ServiceError
            {
                Code = ErrorCodes.Unauthenticated,
                Message = "Current user is not authenticated."
            });
        }

        var user = await _listingsOwnerStore.FindUserByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return ServiceResult<Listing>.Failure(new ServiceError
            {
                Code = ErrorCodes.Unauthenticated,
                Message = "Current user is not authenticated."
            });
        }

        if (user.IsBlocked)
        {
            return ServiceResult<Listing>.Failure(new ServiceError
            {
                Code = ErrorCodes.UserBlocked,
                Message = "Blocked users cannot manage listing images."
            });
        }

        var listing = await _listingsOwnerStore.FindListingByIdWithImagesAsync(listingId, cancellationToken);
        if (listing is null)
        {
            return ServiceResult<Listing>.Failure(new ServiceError
            {
                Code = ErrorCodes.ListingNotFound,
                Message = "Listing was not found."
            });
        }

        if (listing.OwnerId != user.Id)
        {
            return ServiceResult<Listing>.Failure(new ServiceError
            {
                Code = ErrorCodes.ListingForbidden,
                Message = "Only the listing owner can manage images."
            });
        }

        // Images cannot be changed on an archived listing. ReplaceAsync in particular would flip the
        // listing back to PendingApproval, silently un-archiving it — the owner must restore it first.
        if (listing.Status == ListingStatus.Archived)
        {
            return ServiceResult<Listing>.Failure(new ServiceError
            {
                Code = ErrorCodes.ArchivedListing,
                Message = "Archived listings cannot have their images changed. Restore the listing first."
            });
        }

        return ServiceResult<Listing>.Success(listing);
    }

    private static ListingImageResponse ToResponse(ListingImage image) => new()
    {
        Id = image.Id,
        Url = image.Url,
        IsPrimary = image.IsPrimary,
        SortOrder = image.SortOrder
    };

    private static ServiceResult<IReadOnlyCollection<ListingImageResponse>> Failure(string code, string message) =>
        ServiceResult<IReadOnlyCollection<ListingImageResponse>>.Failure(new ServiceError
        {
            Code = code,
            Message = message
        });

    private static ServiceResult<IReadOnlyCollection<ListingImageResponse>> Failure(ServiceError error) =>
        ServiceResult<IReadOnlyCollection<ListingImageResponse>>.Failure(error);

    private sealed record BufferedUpload(MemoryStream Content, string FileName, string ContentType);
}
