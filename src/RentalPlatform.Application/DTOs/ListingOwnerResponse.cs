namespace RentalPlatform.Application.DTOs;

public sealed class ListingOwnerResponse
{
    public Guid Id { get; init; }
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string? AvatarUrl { get; init; }
    // ADR-056: the owner deleted their account (names above are then empty, avatar null). Only an admin can
    // reach this: the deletion archives the listings and the public catalogue serves live owners only.
    public bool IsDeleted { get; init; }
}
