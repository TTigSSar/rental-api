namespace RentalPlatform.Application.DTOs;

/// <summary>
/// One row of the admin moderation queue. A superset of the legacy
/// <see cref="PendingListingForReviewResponse"/> shape — carries everything that DTO has, plus
/// photo/rejection/moderation metadata and an owner-trust block so the queue card can render
/// without a follow-up request per listing.
/// </summary>
public sealed class AdminListingSummaryResponse
{
    public Guid Id { get; init; }

    public Guid OwnerId { get; init; }
    public string OwnerEmail { get; init; } = string.Empty;
    public string OwnerFirstName { get; init; } = string.Empty;
    public string OwnerLastName { get; init; } = string.Empty;

    public Guid CategoryId { get; init; }
    public string CategoryName { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public decimal PricePerDay { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public string? AddressLine { get; init; }

    public int? AgeFromMonths { get; init; }
    public int? AgeToMonths { get; init; }
    public string? Condition { get; init; }
    public string? HygieneNotes { get; init; }
    public string? SafetyNotes { get; init; }
    public decimal? DepositAmount { get; init; }

    public IReadOnlyCollection<ListingImageResponse> Images { get; init; } = Array.Empty<ListingImageResponse>();

    public DateTime CreatedAt { get; init; }

    // --- new in the Phase 1 admin console redesign ---

    public int PhotoCount { get; init; }
    public string? PrimaryImageUrl { get; init; }

    public string? RejectionReasonCode { get; init; }
    public string? RejectionNote { get; init; }
    public DateTime? ModeratedAt { get; init; }

    // Owner trust block — lets the moderator judge the owner's track record without leaving the
    // queue. Computed with grouped queries, not per-row lookups (see AdminListingsStore).
    public string? OwnerAvatarUrl { get; init; }
    public bool OwnerIsIdConfirmed { get; init; }
    public decimal? OwnerRating { get; init; }
    public int OwnerReviewCount { get; init; }
    public int OwnerListingCount { get; init; }
    public int OwnerCompletedRentals { get; init; }
    public DateTime OwnerJoinedAt { get; init; }

    // --- new in the Phase 6 admin console redesign: "Needs category fix" ---

    // Both null unless the listing is PendingApproval and the keyword rule's best-scoring category
    // differs from CategoryId — see AdminListingsService's suggestion rule for the exact scoring.
    public Guid? SuggestedCategoryId { get; init; }
    public string? SuggestedCategoryName { get; init; }
}
