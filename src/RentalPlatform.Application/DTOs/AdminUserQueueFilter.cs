namespace RentalPlatform.Application.DTOs;

/// <summary>
/// Query-string filter for GET /api/admin/users. Status is a loose string ("all" | "pending" |
/// "suspended", case-insensitive) matching the design's three tabs — "active" is a valid row
/// status but not a filter value the UI exposes. Unrecognised or omitted values default to "all".
/// Sort is also a loose string ("name" | "newest" | "listings" | "rentals" | "flags",
/// case-insensitive); unrecognised/omitted defaults to "name". Mapping happens in
/// AdminUsersService, same convention as AdminListingQueueFilter.Status.
/// </summary>
public sealed class AdminUserQueueFilter
{
    public string? Status { get; init; }
    public string? Search { get; init; }
    public string? Sort { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}
