using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
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

        // Not "admin.category_not_found": that code is reserved (see AdminCategoriesService) for a
        // ROUTE id that doesn't resolve (404). This is a category reference inside the request
        // BODY (the recategorise target), a bad-payload situation → 400, same code/status pairing
        // AdminCategoriesService uses for its own request-value category reference (the delete
        // endpoint's reassignToCategoryId).
        public const string CategoryTargetNotFound = "admin.category_reassign_target_not_found";
        public const string InvalidRejectReason = "admin.invalid_reject_reason";
    }

    private const int DefaultPage = 1;
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;
    private const int MaxTargetLabelLength = 300;

    // Admin console Phase 6 ("Needs category fix"): a title hit is a much stronger signal than a
    // description hit, so it outweighs it 3:1. Named constants (not magic numbers inline) so the
    // weighting is one obvious place to tune.
    private const int TitleKeywordWeight = 3;
    private const int DescriptionKeywordWeight = 1;

    // ModerationLogEntry.DetailJson is HasMaxLength(2000). The default JavaScriptEncoder escapes
    // every non-ASCII character (Armenian/Russian rejection notes included) as \uXXXX — 6 chars
    // per source character — so an unbounded note can blow the column even though it fits
    // RejectListingRequest's own 1000-char limit (RejectionReasonCatalog.MaxReasonLength). 300
    // chars of note, worst case fully escaped, plus the {"reasonCode":"...","note":"..."} skeleton
    // and a worst-case reasonCode, stays comfortably under 2000. Truncating the note itself (not
    // the serialised JSON) keeps the emitted string valid JSON.
    private const int MaxDetailNoteLength = 300;

    // UnsafeRelaxedJsonEscaping doesn't escape Armenian/Cyrillic/etc (only HTML-sensitive
    // characters), which shrinks the common case a lot — but MaxDetailNoteLength above is the
    // actual guarantee, not this encoder choice.
    private static readonly JsonSerializerOptions DetailJsonOptions =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly ICurrentUserContext _currentUserContext;
    private readonly IAdminListingsStore _adminListingsStore;
    private readonly IReviewsStore _reviewsStore;
    private readonly IModerationLogStore _moderationLogStore;
    private readonly IEmailService _emailService;
    private readonly INotificationEmitter _notificationEmitter;

    public AdminListingsService(
        ICurrentUserContext currentUserContext,
        IAdminListingsStore adminListingsStore,
        IReviewsStore reviewsStore,
        IModerationLogStore moderationLogStore,
        IEmailService emailService,
        INotificationEmitter notificationEmitter)
    {
        _currentUserContext = currentUserContext;
        _adminListingsStore = adminListingsStore;
        _reviewsStore = reviewsStore;
        _moderationLogStore = moderationLogStore;
        _emailService = emailService;
        _notificationEmitter = notificationEmitter;
    }

    public async Task<ServiceResult<IReadOnlyCollection<PendingListingForReviewResponse>>> GetPendingAsync(
        CancellationToken cancellationToken = default)
    {
        var adminResult = await EnsureAdminAsync(cancellationToken);
        if (!adminResult.IsSuccess)
        {
            return ServiceResult<IReadOnlyCollection<PendingListingForReviewResponse>>.Failure(adminResult.Error!);
        }

        // Unchanged behaviour: the legacy unpaged "all pending listings" call, kept as its own
        // store round-trip (not routed through GetListingsPageAsync's Skip/Take/MaxPageSize
        // clamp) so this endpoint's contract — an unpaged array — cannot regress for the existing
        // frontend/tests even as the paged queue below is added alongside it.
        var listings = await _adminListingsStore.GetPendingListingsAsync(cancellationToken);
        var response = listings.Select(MapToPendingReview).ToList();

        return ServiceResult<IReadOnlyCollection<PendingListingForReviewResponse>>.Success(response);
    }

    public async Task<ServiceResult<AdminListingQueueResponse>> GetQueueAsync(
        AdminListingQueueFilter filter,
        CancellationToken cancellationToken = default)
    {
        var adminResult = await EnsureAdminAsync(cancellationToken);
        if (!adminResult.IsSuccess)
        {
            return ServiceResult<AdminListingQueueResponse>.Failure(adminResult.Error!);
        }

        var status = ParseStatus(filter.Status);
        var page = filter.Page < 1 ? DefaultPage : filter.Page;
        var pageSize = filter.PageSize < 1 ? DefaultPageSize : Math.Min(filter.PageSize, MaxPageSize);
        var search = string.IsNullOrWhiteSpace(filter.Search) ? null : filter.Search.Trim();

        var pageResult = await _adminListingsStore.GetListingsPageAsync(status, search, page, pageSize, cancellationToken);
        var counts = await _adminListingsStore.GetStatusCountsAsync(search, cancellationToken);

        var ownerIds = pageResult.Items.Select(l => l.OwnerId).Distinct().ToList();
        var ratingAggregates = await _reviewsStore.GetOwnerRatingAggregatesAsync(ownerIds, cancellationToken);
        var listingStats = await _adminListingsStore.GetOwnerListingStatsAsync(ownerIds, cancellationToken);

        // The keyword table is only ever consulted for PendingApproval listings (see
        // ComputeSuggestedCategory), so skip the extra round-trip entirely when this page has none
        // — e.g. browsing the Approved/Rejected tabs.
        var keywordMatchers = pageResult.Items.Any(l => l.Status == ListingStatus.PendingApproval)
            ? await GetKeywordMatchersAsync(cancellationToken)
            : Array.Empty<CategoryKeywordMatcher>();

        var items = pageResult.Items
            .Select(listing => MapToSummary(listing, ratingAggregates, listingStats, keywordMatchers))
            .ToList();

        var totalPages = pageSize == 0 ? 0 : (int)Math.Ceiling(pageResult.TotalCount / (double)pageSize);

        return ServiceResult<AdminListingQueueResponse>.Success(new AdminListingQueueResponse
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = pageResult.TotalCount,
            TotalPages = totalPages,
            Counts = counts
        });
    }

    public async Task<ServiceResult<AdminListingDetailResponse>> GetDetailAsync(
        Guid listingId,
        CancellationToken cancellationToken = default)
    {
        var adminResult = await EnsureAdminAsync(cancellationToken);
        if (!adminResult.IsSuccess)
        {
            return ServiceResult<AdminListingDetailResponse>.Failure(adminResult.Error!);
        }

        var listing = await _adminListingsStore.FindListingWithImagesByIdAsync(listingId, cancellationToken);
        if (listing is null)
        {
            return Failure<AdminListingDetailResponse>(ErrorCodes.ListingNotFound, "Listing was not found.");
        }

        var detail = await BuildDetailAsync(listing, cancellationToken);
        return ServiceResult<AdminListingDetailResponse>.Success(detail);
    }

    public async Task<ServiceResult<AdminListingDetailResponse>> UpdateCategoryAsync(
        Guid listingId,
        Guid categoryId,
        CancellationToken cancellationToken = default)
    {
        var adminResult = await EnsureAdminAsync(cancellationToken);
        if (!adminResult.IsSuccess)
        {
            return ServiceResult<AdminListingDetailResponse>.Failure(adminResult.Error!);
        }

        var admin = adminResult.Value!;

        var listing = await _adminListingsStore.FindListingWithImagesByIdAsync(listingId, cancellationToken);
        if (listing is null)
        {
            return Failure<AdminListingDetailResponse>(ErrorCodes.ListingNotFound, "Listing was not found.");
        }

        // A miscategorised live listing must be fixable, so both Pending and already-Approved are
        // allowed; Draft/Archived are not queue states an admin should be recategorising from.
        if (listing.Status != ListingStatus.PendingApproval && listing.Status != ListingStatus.Approved)
        {
            return Failure<AdminListingDetailResponse>(
                ErrorCodes.InvalidStatus, "Only pending or approved listings can be recategorised.");
        }

        if (listing.CategoryId == categoryId)
        {
            // No-op: already the target category. Still 200, no log entry (nothing changed).
            var unchangedDetail = await BuildDetailAsync(listing, cancellationToken);
            return ServiceResult<AdminListingDetailResponse>.Success(unchangedDetail);
        }

        var category = await _adminListingsStore.FindCategoryByIdAsync(categoryId, cancellationToken);
        if (category is null)
        {
            return Failure<AdminListingDetailResponse>(ErrorCodes.CategoryTargetNotFound, "Category was not found.");
        }

        var fromCategoryName = listing.Category.Name;
        listing.CategoryId = category.Id;
        listing.Category = category;
        listing.UpdatedAt = DateTime.UtcNow;

        await _adminListingsStore.SaveChangesAsync(cancellationToken);

        await _moderationLogStore.AppendAsync(new ModerationLogEntry
        {
            Id = Guid.NewGuid(),
            ActorUserId = admin.Id,
            Action = ModerationAction.ListingRecategorised,
            TargetType = ModerationTargetType.Listing,
            TargetId = listing.Id,
            TargetLabel = Truncate(listing.Title),
            DetailJson = JsonSerializer.Serialize(
                new { fromCategory = Truncate(fromCategoryName), toCategory = Truncate(category.Name) },
                DetailJsonOptions),
            CreatedAt = DateTime.UtcNow
        }, cancellationToken);

        var detail = await BuildDetailAsync(listing, cancellationToken);
        return ServiceResult<AdminListingDetailResponse>.Success(detail);
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
        listing.RejectionReasonCode = null;
        listing.RejectionNote = null;
        listing.ModeratedAt = now;
        listing.ModeratedByUserId = admin.Id;
        listing.UpdatedAt = now;

        await _adminListingsStore.SaveChangesAsync(cancellationToken);

        await _moderationLogStore.AppendAsync(new ModerationLogEntry
        {
            Id = Guid.NewGuid(),
            ActorUserId = admin.Id,
            Action = ModerationAction.ListingApproved,
            TargetType = ModerationTargetType.Listing,
            TargetId = listing.Id,
            TargetLabel = Truncate(listing.Title),
            DetailJson = null,
            CreatedAt = now
        }, cancellationToken);

        // IEmailService contract: never throws. Moderation success is not conditional on delivery.
        await _emailService.SendListingApprovedAsync(
            listing.Owner.Email,
            $"{listing.Owner.FirstName} {listing.Owner.LastName}".Trim(),
            listing.Title,
            cancellationToken);

        // Best-effort: notify the owner their listing is live.
        await _notificationEmitter.ListingApprovedAsync(listing, cancellationToken);

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
        string reasonCode,
        string? note,
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

        var trimmedCode = reasonCode.Trim();

        // Defence in depth: RejectListingRequest.Validate already rejects an unknown code at the
        // HTTP boundary, but the service is the layer that must not trust its caller — the same
        // pattern as EnsureAdminAsync re-checking the role the controller's [Authorize] already gates.
        if (!RejectionReasonCatalog.IsKnownCode(trimmedCode))
        {
            return Failure<ModerateListingResponse>(ErrorCodes.InvalidRejectReason, "Rejection reason is invalid.");
        }

        var trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        var trimmedReason = RejectionReasonCatalog.Compose(trimmedCode, trimmedNote);
        var now = DateTime.UtcNow;
        listing.Status = ListingStatus.Rejected;
        listing.RejectionReason = trimmedReason;
        listing.RejectionReasonCode = trimmedCode;
        listing.RejectionNote = trimmedNote;
        listing.ModeratedAt = now;
        listing.ModeratedByUserId = admin.Id;
        listing.UpdatedAt = now;

        await _adminListingsStore.SaveChangesAsync(cancellationToken);

        await _moderationLogStore.AppendAsync(new ModerationLogEntry
        {
            Id = Guid.NewGuid(),
            ActorUserId = admin.Id,
            Action = ModerationAction.ListingRejected,
            TargetType = ModerationTargetType.Listing,
            TargetId = listing.Id,
            TargetLabel = Truncate(listing.Title),
            DetailJson = JsonSerializer.Serialize(
                new { reasonCode = trimmedCode, note = TruncateOrNull(trimmedNote) },
                DetailJsonOptions),
            CreatedAt = now
        }, cancellationToken);

        // IEmailService contract: never throws. Moderation success is not conditional on delivery.
        await _emailService.SendListingRejectedAsync(
            listing.Owner.Email,
            $"{listing.Owner.FirstName} {listing.Owner.LastName}".Trim(),
            listing.Title,
            trimmedReason,
            cancellationToken);

        // Best-effort: notify the owner that changes are needed.
        await _notificationEmitter.ListingRejectedAsync(listing, trimmedReason, cancellationToken);

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

    // "Pending" | "Approved" | "Rejected", case-insensitive; anything else (including null/empty,
    // and Draft/Archived — not queue states) defaults to Pending, matching the documented
    // "status optional, defaults to Pending" contract.
    private static ListingStatus ParseStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "approved" => ListingStatus.Approved,
        "rejected" => ListingStatus.Rejected,
        _ => ListingStatus.PendingApproval
    };

    private async Task<AdminListingDetailResponse> BuildDetailAsync(Listing listing, CancellationToken cancellationToken)
    {
        var ownerIds = new[] { listing.OwnerId };
        var ratingAggregates = await _reviewsStore.GetOwnerRatingAggregatesAsync(ownerIds, cancellationToken);
        var listingStats = await _adminListingsStore.GetOwnerListingStatsAsync(ownerIds, cancellationToken);

        ratingAggregates.TryGetValue(listing.OwnerId, out var rating);
        listingStats.TryGetValue(listing.OwnerId, out var stats);

        var orderedImages = listing.Images
            .OrderByDescending(img => img.IsPrimary)
            .ThenBy(img => img.SortOrder)
            .Select(MapImage)
            .ToList();

        var keywordMatchers = listing.Status == ListingStatus.PendingApproval
            ? await GetKeywordMatchersAsync(cancellationToken)
            : Array.Empty<CategoryKeywordMatcher>();
        var suggestion = ComputeSuggestedCategory(listing, keywordMatchers);

        return new AdminListingDetailResponse
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
            Images = orderedImages,
            CreatedAt = listing.CreatedAt,
            PhotoCount = orderedImages.Count,
            PrimaryImageUrl = orderedImages.FirstOrDefault()?.Url,
            RejectionReasonCode = listing.RejectionReasonCode,
            RejectionNote = listing.RejectionNote,
            ModeratedAt = listing.ModeratedAt,
            OwnerAvatarUrl = listing.Owner.AvatarUrl,
            OwnerIsIdConfirmed = listing.Owner.IsIdConfirmed,
            OwnerRating = rating is null || rating.Count == 0 ? null : (decimal?)rating.Average,
            OwnerReviewCount = rating?.Count ?? 0,
            OwnerListingCount = stats?.ApprovedListingCount ?? 0,
            OwnerCompletedRentals = stats?.CompletedRentalCount ?? 0,
            OwnerJoinedAt = listing.Owner.CreatedAt,
            Status = listing.Status,
            OwnerOpenReportCount = stats?.OpenReportCount ?? 0,
            SuggestedCategoryId = suggestion.CategoryId,
            SuggestedCategoryName = suggestion.CategoryName
        };
    }

    private static AdminListingSummaryResponse MapToSummary(
        Listing listing,
        IReadOnlyDictionary<Guid, RatingAggregate> ratingAggregates,
        IReadOnlyDictionary<Guid, OwnerListingStats> listingStats,
        IReadOnlyList<CategoryKeywordMatcher> keywordMatchers)
    {
        ratingAggregates.TryGetValue(listing.OwnerId, out var rating);
        listingStats.TryGetValue(listing.OwnerId, out var stats);

        var orderedImages = listing.Images
            .OrderByDescending(img => img.IsPrimary)
            .ThenBy(img => img.SortOrder)
            .Select(MapImage)
            .ToList();

        var suggestion = ComputeSuggestedCategory(listing, keywordMatchers);

        return new AdminListingSummaryResponse
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
            Images = orderedImages,
            CreatedAt = listing.CreatedAt,
            PhotoCount = orderedImages.Count,
            PrimaryImageUrl = orderedImages.FirstOrDefault()?.Url,
            RejectionReasonCode = listing.RejectionReasonCode,
            RejectionNote = listing.RejectionNote,
            ModeratedAt = listing.ModeratedAt,
            OwnerAvatarUrl = listing.Owner.AvatarUrl,
            OwnerIsIdConfirmed = listing.Owner.IsIdConfirmed,
            OwnerRating = rating is null || rating.Count == 0 ? null : (decimal?)rating.Average,
            OwnerReviewCount = rating?.Count ?? 0,
            OwnerListingCount = stats?.ApprovedListingCount ?? 0,
            OwnerCompletedRentals = stats?.CompletedRentalCount ?? 0,
            OwnerJoinedAt = listing.Owner.CreatedAt,
            SuggestedCategoryId = suggestion.CategoryId,
            SuggestedCategoryName = suggestion.CategoryName
        };
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
            .Select(MapImage)
            .ToList(),
        CreatedAt = listing.CreatedAt
    };

    private static ListingImageResponse MapImage(ListingImage img) => new()
    {
        Id = img.Id,
        Url = img.Url,
        IsPrimary = img.IsPrimary,
        SortOrder = img.SortOrder
    };

    private static string Truncate(string value) =>
        value.Length <= MaxTargetLabelLength ? value : value[..MaxTargetLabelLength];

    // Bounds the note that goes into DetailJson — see MaxDetailNoteLength. Truncates the raw note
    // itself (before serialisation) so the emitted JSON always stays well-formed.
    private static string? TruncateOrNull(string? value) =>
        value is null || value.Length <= MaxDetailNoteLength ? value : value[..MaxDetailNoteLength];

    private static ServiceResult<T> Failure<T>(string code, string message) =>
        ServiceResult<T>.Failure(new ServiceError { Code = code, Message = message });

    // --- Admin console Phase 6: "Needs category fix" suggestion rule ---

    // One compiled whole-word matcher per keyword row, built once per request (see
    // GetKeywordMatchersAsync) and reused across every listing on the page — the store call is the
    // only per-request DB round trip; everything after that is in-memory.
    private sealed record CategoryKeywordMatcher(Guid CategoryId, string CategoryName, int CategoryDisplayOrder, Regex Pattern);

    private async Task<IReadOnlyList<CategoryKeywordMatcher>> GetKeywordMatchersAsync(CancellationToken cancellationToken)
    {
        var entries = await _adminListingsStore.GetCategoryKeywordsAsync(cancellationToken);
        return BuildMatchers(entries);
    }

    // (?<![\p{L}\p{N}])keyword(?![\p{L}\p{N}]) — a "whole word" match that also works for
    // multi-word/hyphenated keywords ("balance bike", "ride-on"): it only requires that the
    // character immediately before/after the match (if any) not be a letter or digit, rather than
    // relying on \b, which gets confused by internal punctuation like the hyphen in "ride-on".
    // This is what stops "car" from matching inside "carpet" while still matching "car" at a word
    // boundary next to punctuation or the string edge.
    private static IReadOnlyList<CategoryKeywordMatcher> BuildMatchers(IReadOnlyCollection<CategoryKeywordEntry> entries) =>
        entries
            .Select(entry => new CategoryKeywordMatcher(
                entry.CategoryId,
                entry.CategoryName,
                entry.CategoryDisplayOrder,
                new Regex(
                    $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(entry.Keyword)}(?![\p{{L}}\p{{N}}])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            .ToList();

    // The suggestion rule: score every category by its matched keywords (a title hit counts
    // TitleKeywordWeight, a description hit counts DescriptionKeywordWeight — presence per keyword
    // per field, not occurrence count, so keyword-stuffing a single word can't inflate the score),
    // take the single highest-scoring category, and suggest it only when it's non-zero AND
    // different from the listing's own category. Ties break on the category's DisplayOrder, then
    // its Id, so the same listing never flip-flops between page loads. Only ever computed for
    // PendingApproval listings — an approved listing is settled and must not nag.
    private static (Guid? CategoryId, string? CategoryName) ComputeSuggestedCategory(
        Listing listing, IReadOnlyList<CategoryKeywordMatcher> keywordMatchers)
    {
        if (listing.Status != ListingStatus.PendingApproval || keywordMatchers.Count == 0)
        {
            return (null, null);
        }

        var best = keywordMatchers
            .GroupBy(matcher => new { matcher.CategoryId, matcher.CategoryName, matcher.CategoryDisplayOrder })
            .Select(group => new
            {
                group.Key.CategoryId,
                group.Key.CategoryName,
                group.Key.CategoryDisplayOrder,
                Score = group.Sum(matcher =>
                    (matcher.Pattern.IsMatch(listing.Title) ? TitleKeywordWeight : 0) +
                    (matcher.Pattern.IsMatch(listing.Description) ? DescriptionKeywordWeight : 0))
            })
            .Where(scored => scored.Score > 0)
            .OrderByDescending(scored => scored.Score)
            .ThenBy(scored => scored.CategoryDisplayOrder)
            .ThenBy(scored => scored.CategoryId)
            .FirstOrDefault();

        if (best is null || best.CategoryId == listing.CategoryId)
        {
            return (null, null);
        }

        return (best.CategoryId, best.CategoryName);
    }
}
