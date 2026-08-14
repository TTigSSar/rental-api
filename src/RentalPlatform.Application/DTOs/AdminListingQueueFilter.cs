namespace RentalPlatform.Application.DTOs;

/// <summary>
/// Query-string filter for GET /api/admin/listings. Status is a loose string ("Pending" |
/// "Approved" | "Rejected", case-insensitive) rather than the ListingStatus enum directly,
/// because the public admin-queue vocabulary intentionally excludes Draft/Archived — those
/// aren't queue states — and binding straight to the domain enum would let a caller pass them.
/// Unrecognised or omitted values default to Pending; mapping happens in AdminListingsService.
/// </summary>
public sealed class AdminListingQueueFilter
{
    public string? Status { get; init; }
    public string? Search { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}
