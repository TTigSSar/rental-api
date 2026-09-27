namespace RentalPlatform.Application.DTOs;

/// <summary>
/// Query-string filter for GET /api/admin/messages/threads. Filter is a loose string
/// ("all" | "unread" | "needsReply", case-insensitive); unrecognised or omitted values default
/// to "all". Search matches the member's name/email. Mapping happens in AdminMessagesService,
/// same convention as AdminUserQueueFilter.Status.
/// </summary>
public sealed class AdminMessageThreadFilter
{
    public string? Filter { get; init; }
    public string? Search { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}
