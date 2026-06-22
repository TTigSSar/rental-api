namespace RentalPlatform.Application.DTOs;

public sealed class PublicUserProfileResponse
{
    public Guid Id { get; init; }
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string? AvatarUrl { get; init; }
    public DateTime MemberSince { get; init; }
    public double AverageRating { get; init; }
    public int ReviewCount { get; init; }
    public int ActiveListingsCount { get; init; }

    // Trust surface
    public string? Location { get; init; }
    public bool IsVerified { get; init; }
    public bool IsIdConfirmed { get; init; }
    public bool IsEmailPhoneConfirmed { get; init; }

    // As Owner
    public double? OwnerRating { get; init; }
    public int OwnerReviewCount { get; init; }
    public int CompletedRentalsAsOwner { get; init; }
    public int? ResponseRate { get; init; }
    public double? HygieneScore { get; init; }
    public List<HygieneStandardDto> HygieneStandards { get; init; } = [];

    // As Renter
    public double? RenterRating { get; init; }
    public int RenterReviewCount { get; init; }
    public int CompletedRentalsAsRenter { get; init; }
    public int? OnTimeReturnRate { get; init; }
    public int DamageClaims { get; init; }
    public List<ReliabilityMetricDto> ReliabilityMetrics { get; init; } = [];
}

public sealed record HygieneStandardDto(string Id, string Label, string IconKey, bool Met);
public sealed record ReliabilityMetricDto(string Id, string Label, int Value);
