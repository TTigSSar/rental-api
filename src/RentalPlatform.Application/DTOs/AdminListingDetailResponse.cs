using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.DTOs;

/// <summary>
/// The single-listing "inspect" dossier. Everything <see cref="AdminListingSummaryResponse"/>
/// has (full ordered Images, Description, AddressLine, DepositAmount, Condition, HygieneNotes,
/// SafetyNotes, AgeFromMonths, AgeToMonths and Currency are already carried by the summary — see
/// the final report for why those aren't repeated as distinct fields here), plus the listing's
/// current Status and the owner's open-report count.
/// </summary>
public sealed class AdminListingDetailResponse
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

    public int PhotoCount { get; init; }
    public string? PrimaryImageUrl { get; init; }

    public string? RejectionReasonCode { get; init; }
    public string? RejectionNote { get; init; }
    public DateTime? ModeratedAt { get; init; }

    public string? OwnerAvatarUrl { get; init; }
    public bool OwnerIsIdConfirmed { get; init; }
    public decimal? OwnerRating { get; init; }
    public int OwnerReviewCount { get; init; }
    public int OwnerListingCount { get; init; }
    public int OwnerCompletedRentals { get; init; }
    public DateTime OwnerJoinedAt { get; init; }

    // --- new for the dossier only ---

    public ListingStatus Status { get; init; }

    // Count of Open reports filed against the owner.
    public int OwnerOpenReportCount { get; init; }

    // --- new in the Phase 6 admin console redesign: "Needs category fix" ---

    // Both null unless Status is PendingApproval and the keyword rule's best-scoring category
    // differs from CategoryId — see AdminListingsService's suggestion rule for the exact scoring.
    public Guid? SuggestedCategoryId { get; init; }
    public string? SuggestedCategoryName { get; init; }
}
