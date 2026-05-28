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
}
