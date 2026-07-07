namespace RentalPlatform.Application.DTOs;

public sealed class NotificationActorResponse
{
    public string Name { get; init; } = string.Empty;
    public string? AvatarUrl { get; init; }
    public bool Verified { get; init; }
    public bool System { get; init; }
    public string? SystemIcon { get; init; }
}

public sealed class NotificationToyResponse
{
    public string? ImageUrl { get; init; }
    public string Title { get; init; } = string.Empty;
}

public sealed class NotificationActionResponse
{
    public string Label { get; init; } = string.Empty;
    public string DeepLink { get; init; } = string.Empty;
}

public sealed class NotificationResponse
{
    public Guid Id { get; init; }
    public string Kind { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public string? Meta { get; init; }
    public DateTime CreatedAt { get; init; }
    public bool Read { get; init; }
    public bool Urgent { get; init; }
    public NotificationActorResponse Actor { get; init; } = new();
    public NotificationToyResponse? Toy { get; init; }
    public NotificationActionResponse? PrimaryAction { get; init; }
    public NotificationActionResponse? SecondaryAction { get; init; }
}

public sealed class NotificationCountsResponse
{
    public int All { get; init; }
    public int Unread { get; init; }
    public int Action { get; init; }
}

public sealed class NotificationFeedResponse
{
    public IReadOnlyCollection<NotificationResponse> Items { get; init; } = Array.Empty<NotificationResponse>();
    public string? NextCursor { get; init; }
    public NotificationCountsResponse Counts { get; init; } = new();
}

public sealed class NotificationUnreadCountResponse
{
    public int UnreadCount { get; init; }
}
