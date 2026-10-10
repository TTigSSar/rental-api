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
        public const string HomePointRequired = "listing.home_point_required";
    }

    private readonly ICurrentUserContext _currentUserContext;
    private readonly IListingsOwnerStore _listingsOwnerStore;

    public ListingsOwnerService(
        ICurrentUserContext currentUserContext,
        IListingsOwnerStore listingsOwnerStore)
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

        // Every listing inherits the owner's home point verbatim (home-point model) — Create is the one
        // place that gate is enforced; Update/archive/restore/resubmit keep working for owners who
        // signed up before this feature shipped (M-038).
        if (user.HomeLatitude is null || user.HomeLongitude is null)
        {
            return ServiceResult<CreateListingResponse>.Failure(new ServiceError
            {
                Code = ErrorCodes.HomePointRequired,
                Message = "Set your home point before listing a toy."
            });
        }

        var now = DateTime.UtcNow;
        var city = await ResolveCityAsync(user, cancellationToken);
        var deliveryOptions = DeliveryOptionsMapper.Combine(request.DeliveryTypes, request.DeliveryType);
        var listing = new Listing
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            CategoryId = request.CategoryId,
            Title = request.Title.Trim(),
            Description = request.Description.Trim(),
            PricePerDay = request.PricePerDay,
            // Default to Daily when the period is omitted (kept in sync with the entity/DB default).
            PriceUnit = request.PriceUnit ?? PriceUnit.Daily,
            Currency = string.IsNullOrWhiteSpace(request.Currency) ? "AMD" : request.Currency.Trim().ToUpperInvariant(),
            AddressLine = NormalizeOptional(request.AddressLine),
            // Every location field — Country, City, the exact and public coordinates, the district —
            // is set by ApplyOwnerHomePoint below and by nothing else, so there is exactly one
            // expression in this method that decides where a new listing is. See the call site for
            // why it runs inside the insert's transaction rather than here.
            AgeFromMonths = request.AgeFromMonths,
            AgeToMonths = request.AgeToMonths,
            Condition = NormalizeOptional(request.Condition),
            HygieneNotes = NormalizeOptional(request.HygieneNotes),
            SafetyNotes = NormalizeOptional(request.SafetyNotes),
            CompensationAmount = request.CompensationAmount,
            MinRentalDays = request.MinRentalDays,
            DeliveryOptions = deliveryOptions,
            DeliveryType = DeliveryOptionsMapper.ToLegacy(deliveryOptions),
            Status = ListingStatus.PendingApproval,
            CreatedAt = now,
            UpdatedAt = now
        };

        // The home point is read AGAIN here, from inside the insert's own transaction, and the
        // location is written from that fresh row — the `user` instance above is only good enough to
        // answer "may this owner publish at all".
        //
        // Why: between the gate above and this insert there were two more round-trips, and a
        // concurrent PUT /api/auth/me/home-point relocates only the listings that are already
        // committed. A create that lost that race committed the owner's PREVIOUS point and stayed
        // there — nothing re-reconciles a listing's location afterwards except
        // ListingLocationBackfillRunner's second pass, which runs once per process start, and
        // production restarts on the order of weeks. The row would keep advertising the area the
        // owner had left, in /listings, in the map pins and in the radius filter.
        var inserted = await _listingsOwnerStore.TryAddListingAtOwnerHomePointAsync(
            listing,
            owner => ApplyOwnerHomePoint(listing, owner, city),
            cancellationToken);

        if (!inserted)
        {
            // The owner cleared their home point while this create was in flight (DELETE
            // me/home-point). Same answer as the gate above: there is no point to publish at, and
            // no field on the request can fix it.
            return ServiceResult<CreateListingResponse>.Failure(new ServiceError
            {
                Code = ErrorCodes.HomePointRequired,
                Message = "Set your home point before listing a toy."
            });
        }

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
                PriceUnit = listing.PriceUnit,
                Currency = listing.Currency,
                Country = listing.Country,
                City = listing.City,
                AgeFromMonths = listing.AgeFromMonths,
                AgeToMonths = listing.AgeToMonths,
                Condition = listing.Condition,
                HygieneNotes = listing.HygieneNotes,
                SafetyNotes = listing.SafetyNotes,
                CompensationAmount = listing.CompensationAmount,
                MinRentalDays = listing.MinRentalDays,
                DeliveryType = listing.DeliveryType,
                DeliveryTypes = DeliveryOptionsMapper.Expand(listing.DeliveryOptions, listing.DeliveryType),
                Status = listing.Status,
                RejectionReason = listing.RejectionReason,
                Rejection = listing.Status == ListingStatus.Rejected && listing.RejectionReasonCode is { } code
                    ? new ListingRejectionResponse
                    {
                        ReasonCode = code,
                        ReasonLabel = RejectionReasonCatalog.LabelFor(code),
                        Note = listing.RejectionNote,
                        ModeratorName = null,
                        ModeratedAt = listing.ModeratedAt
                    }
                    : null,
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

        if (await _listingsOwnerStore.FindUserByIdAsync(ownerId, cancellationToken) is not { IsBlocked: false })
        {
            return ServiceResult<bool>.Failure(new ServiceError
            {
                Code = ErrorCodes.UserBlocked,
                Message = "Blocked users cannot modify listings."
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

        if (await _listingsOwnerStore.FindUserByIdAsync(ownerId, cancellationToken) is not { IsBlocked: false })
        {
            return ServiceResult<bool>.Failure(new ServiceError
            {
                Code = ErrorCodes.UserBlocked,
                Message = "Blocked users cannot modify listings."
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

        if (await _listingsOwnerStore.FindUserByIdAsync(ownerId, cancellationToken) is not { IsBlocked: false })
        {
            return ServiceResult<Guid>.Failure(new ServiceError
            {
                Code = ErrorCodes.UserBlocked,
                Message = "Blocked users cannot modify listings."
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
        if (request.PriceUnit is not null) listing.PriceUnit = request.PriceUnit.Value;
        // Country and City are no longer editable — they are derived from the owner's home point on
        // create and re-derived by HomePointService whenever the point moves (home-point model).
        if (request.AgeFromMonths is not null) listing.AgeFromMonths = request.AgeFromMonths;
        if (request.AgeToMonths is not null) listing.AgeToMonths = request.AgeToMonths;
        if (request.CompensationAmount is not null) listing.CompensationAmount = request.CompensationAmount;
        if (request.MinRentalDays is not null) listing.MinRentalDays = request.MinRentalDays;

        if (request.DeliveryTypes is not null)
        {
            var deliveryOptions = DeliveryOptionsMapper.Combine(request.DeliveryTypes, request.DeliveryType);
            listing.DeliveryOptions = deliveryOptions;
            listing.DeliveryType = DeliveryOptionsMapper.ToLegacy(deliveryOptions);
        }
        else if (request.DeliveryType is { } legacyOnlyDeliveryType &&
                 DeliveryOptionsMapper.ShouldApplyLegacyOnlyUpdate(listing.DeliveryOptions, legacyOnlyDeliveryType))
        {
            // Legacy-only submission (stale pre-deploy client) that is a real change — collapse to
            // the single reported flag, same as the pre-fix behaviour. See ShouldApplyLegacyOnlyUpdate
            // for the no-op case (current flags already include the legacy value).
            var deliveryOptions = DeliveryOptionsMapper.Combine(null, legacyOnlyDeliveryType);
            listing.DeliveryOptions = deliveryOptions;
            listing.DeliveryType = DeliveryOptionsMapper.ToLegacy(deliveryOptions);
        }

        if (listing.Status == ListingStatus.Rejected)
        {
            listing.Status = ListingStatus.PendingApproval;
            listing.RejectionReason = null;
            listing.RejectionReasonCode = null;
            listing.RejectionNote = null;
        }
        else if (listing.Status == ListingStatus.Approved && contentChanged)
        {
            listing.Status = ListingStatus.PendingApproval;
        }

        listing.UpdatedAt = DateTime.UtcNow;
        await _listingsOwnerStore.SaveChangesAsync(cancellationToken);

        return ServiceResult<Guid>.Success(listing.Id);
    }

    public async Task<ServiceResult<bool>> ResubmitAsync(
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

        if (await _listingsOwnerStore.FindUserByIdAsync(ownerId, cancellationToken) is not { IsBlocked: false })
        {
            return ServiceResult<bool>.Failure(new ServiceError
            {
                Code = ErrorCodes.UserBlocked,
                Message = "Blocked users cannot modify listings."
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

        // The wizard saves edits via PATCH (which already flips a rejected listing back to pending)
        // and then calls resubmit as the explicit "send back for review" step. Treat an
        // already-pending listing as a successful no-op so that flow is idempotent.
        if (listing.Status == ListingStatus.PendingApproval)
        {
            return ServiceResult<bool>.Success(true);
        }

        if (listing.Status != ListingStatus.Rejected)
        {
            return ServiceResult<bool>.Failure(new ServiceError
            {
                Code = ErrorCodes.InvalidStatus,
                Message = "Only rejected listings can be resubmitted for review."
            });
        }

        listing.Status = ListingStatus.PendingApproval;
        listing.RejectionReason = null;
        listing.RejectionReasonCode = null;
        listing.RejectionNote = null;
        listing.UpdatedAt = DateTime.UtcNow;
        await _listingsOwnerStore.SaveChangesAsync(cancellationToken);

        return ServiceResult<bool>.Success(true);
    }

    // The city a newly-created listing gets, from the owner's home point (home-point model).
    // There is deliberately no geocoder in the correctness path (see IDistrictBoundaryProvider), so
    // the only place name that can ever be *derived* is "the home point is inside Yerevan" — which,
    // now that nothing else can be saved, is the answer for every current account.
    /// <summary>
    /// Copies <paramref name="owner"/>'s home point onto <paramref name="listing"/> — every location
    /// field a new listing has (home-point model). Returns false when that owner has no home point,
    /// which aborts the insert.
    /// </summary>
    /// <remarks>
    /// Deliberately a pure function of the owner row, so it can be applied to a row read inside the
    /// insert's transaction: that is what makes a create concurrent with a home-point move land on
    /// the point the move committed instead of the one this request first read. HomePointService
    /// remains the single writer for a move; this is the create half of the same invariant, and the
    /// two must agree on the City rule below.
    /// </remarks>
    private static bool ApplyOwnerHomePoint(Listing listing, User owner, string cityWhenNoDistrict)
    {
        if (owner.HomeLatitude is null || owner.HomeLongitude is null)
        {
            return false;
        }

        listing.Latitude = owner.HomeLatitude;
        listing.Longitude = owner.HomeLongitude;
        listing.PublicLatitude = owner.HomePublicLatitude;
        listing.PublicLongitude = owner.HomePublicLongitude;
        listing.DistrictId = owner.HomeDistrictId;
        listing.LocationKind = LocationKind.Home;

        // Resolving to a district IS the statement "this point is in Yerevan" — the same rule
        // HomePointService applies when it moves a listing, so a created and a relocated listing
        // cannot end up labelled differently for the same point. cityWhenNoDistrict carries the
        // legacy answer for a district-less home point (see ResolveCityAsync).
        listing.City = owner.HomeDistrictId is not null ? LocationDefaults.YerevanCity : cityWhenNoDistrict;
        listing.Country = LocationDefaults.Country;
        return true;
    }

    private async Task<string> ResolveCityAsync(User owner, CancellationToken cancellationToken)
    {
        // The normal case, and now the only one a new account can be in: a home point can only be
        // saved inside Yerevan (IHomePointService.ValidateForSave), so it always has a district.
        if (owner.HomeDistrictId is not null)
        {
            return LocationDefaults.YerevanCity;
        }

        // Legacy only. The AddUserHomePoint migration derived home points from existing listings
        // without applying the Yerevan rule (M-038 — a rule introduced later must not retroactively
        // break rows that predate it), so an owner migrated from a listing outside Yerevan still has
        // a district-less home point and can still publish.
        //
        // Their existing listings' city is the best information anyone has: by construction such an
        // owner HAS at least one listing (their home point was derived from one), and answering
        // "Yerevan" would be both wrong and inconsistent with the rest of their own catalogue. The
        // YerevanCity default below is therefore unreachable in practice and exists only so this
        // method has a total answer.
        var previousCity = await _listingsOwnerStore.FindMostRecentListingCityAsync(owner.Id, cancellationToken);
        return string.IsNullOrWhiteSpace(previousCity) ? LocationDefaults.YerevanCity : previousCity.Trim();
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
