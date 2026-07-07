using Microsoft.Extensions.Logging;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Infrastructure.Services;

/// <summary>
/// Builds and persists notification rows for domain events. Every method is
/// best-effort: failures are logged and swallowed so a notification problem can
/// never break the booking/moderation action that triggered it.
/// </summary>
public sealed class NotificationEmitter : INotificationEmitter
{
    private const string SystemPlatformName = "ToyRent";
    private const string SystemPlatformIcon = "heart";
    private const string SystemModeratorName = "ToyRent Moderator";
    private const string SystemModeratorIcon = "shield";

    private readonly INotificationsStore _store;
    private readonly ILogger<NotificationEmitter> _logger;

    public NotificationEmitter(INotificationsStore store, ILogger<NotificationEmitter> logger)
    {
        _store = store;
        _logger = logger;
    }

    public Task BookingRequestedAsync(Booking booking, User renter, Listing listing, CancellationToken cancellationToken = default) =>
        EmitAsync(new Notification
        {
            Id = Guid.NewGuid(),
            RecipientId = listing.OwnerId,
            Kind = NotificationKind.Request,
            Category = NotificationCategory.Booking,
            Urgent = true,
            Title = $"New rental request from {renter.FirstName}",
            Body = $"{renter.FirstName} wants your \"{listing.Title}\" for {FormatRange(booking)}. Respond within 24h to keep your fast-reply badge.",
            Meta = BuildMeta(booking, listing.Currency),
            ActorName = FullName(renter),
            ActorAvatarUrl = renter.AvatarUrl,
            ActorVerified = renter.IsIdConfirmed,
            EntityType = NotificationEntityType.Booking,
            EntityId = booking.Id,
            DeepLink = "/bookings/requests",
            ToyTitle = listing.Title,
            ToyImageUrl = PrimaryImageUrl(listing),
            PrimaryActionLabel = "Review request",
            PrimaryActionDeepLink = "/bookings/requests",
            CreatedAt = DateTime.UtcNow
        }, cancellationToken);

    public Task BookingApprovedAsync(Booking booking, User owner, CancellationToken cancellationToken = default) =>
        EmitAsync(new Notification
        {
            Id = Guid.NewGuid(),
            RecipientId = booking.RenterId,
            Kind = NotificationKind.Approved,
            Category = NotificationCategory.Booking,
            Urgent = false,
            Title = $"{owner.FirstName} approved your request",
            Body = $"The \"{booking.Listing.Title}\" is yours for {FormatRange(booking)}. Arrange a pickup time with {owner.FirstName}.",
            Meta = BuildMeta(booking, booking.Listing.Currency),
            ActorName = FullName(owner),
            ActorAvatarUrl = owner.AvatarUrl,
            ActorVerified = owner.IsIdConfirmed,
            EntityType = NotificationEntityType.Booking,
            EntityId = booking.Id,
            DeepLink = $"/bookings/{booking.Id}",
            ToyTitle = booking.Listing.Title,
            ToyImageUrl = PrimaryImageUrl(booking.Listing),
            PrimaryActionLabel = "Arrange pickup",
            PrimaryActionDeepLink = $"/bookings/{booking.Id}",
            CreatedAt = DateTime.UtcNow
        }, cancellationToken);

    public Task BookingDeclinedAsync(Booking booking, User owner, CancellationToken cancellationToken = default) =>
        EmitAsync(new Notification
        {
            Id = Guid.NewGuid(),
            RecipientId = booking.RenterId,
            Kind = NotificationKind.Declined,
            Category = NotificationCategory.Booking,
            Urgent = false,
            Title = $"{owner.FirstName} declined your request",
            Body = string.IsNullOrWhiteSpace(booking.RejectionReason)
                ? $"Your request for \"{booking.Listing.Title}\" was declined. Here are similar toys nearby."
                : $"\"{booking.RejectionReason}\" — here are similar toys nearby.",
            Meta = BuildMeta(booking, booking.Listing.Currency),
            ActorName = FullName(owner),
            ActorAvatarUrl = owner.AvatarUrl,
            ActorVerified = owner.IsIdConfirmed,
            EntityType = NotificationEntityType.Booking,
            EntityId = booking.Id,
            DeepLink = "/listings",
            ToyTitle = booking.Listing.Title,
            ToyImageUrl = PrimaryImageUrl(booking.Listing),
            PrimaryActionLabel = "Find similar toys",
            PrimaryActionDeepLink = "/listings",
            CreatedAt = DateTime.UtcNow
        }, cancellationToken);

    public Task ListingApprovedAsync(Listing listing, CancellationToken cancellationToken = default) =>
        EmitAsync(new Notification
        {
            Id = Guid.NewGuid(),
            RecipientId = listing.OwnerId,
            Kind = NotificationKind.ListingLive,
            Category = NotificationCategory.Listing,
            Urgent = false,
            Title = "Your listing is live",
            Body = $"\"{listing.Title}\" passed moderation. Families can now find and request it.",
            Meta = null,
            ActorName = SystemPlatformName,
            ActorIsSystem = true,
            ActorSystemIcon = SystemPlatformIcon,
            EntityType = NotificationEntityType.Listing,
            EntityId = listing.Id,
            DeepLink = $"/listings/{listing.Id}",
            ToyTitle = listing.Title,
            ToyImageUrl = PrimaryImageUrl(listing),
            PrimaryActionLabel = "View public listing",
            PrimaryActionDeepLink = $"/listings/{listing.Id}",
            CreatedAt = DateTime.UtcNow
        }, cancellationToken);

    public Task ListingRejectedAsync(Listing listing, string? reason, CancellationToken cancellationToken = default) =>
        EmitAsync(new Notification
        {
            Id = Guid.NewGuid(),
            RecipientId = listing.OwnerId,
            Kind = NotificationKind.ListingChanges,
            Category = NotificationCategory.Listing,
            Urgent = true,
            Title = "Changes needed on your listing",
            Body = string.IsNullOrWhiteSpace(reason)
                ? $"\"{listing.Title}\" needs changes before it can go live. Fix the issues and resubmit."
                : $"{reason} Fix the issues on \"{listing.Title}\" and resubmit — fixes jump the review queue.",
            Meta = null,
            ActorName = SystemModeratorName,
            ActorIsSystem = true,
            ActorSystemIcon = SystemModeratorIcon,
            EntityType = NotificationEntityType.Listing,
            EntityId = listing.Id,
            DeepLink = $"/my-listings/{listing.Id}/edit",
            ToyTitle = listing.Title,
            ToyImageUrl = PrimaryImageUrl(listing),
            PrimaryActionLabel = "Edit & resubmit",
            PrimaryActionDeepLink = $"/my-listings/{listing.Id}/edit",
            CreatedAt = DateTime.UtcNow
        }, cancellationToken);

    private async Task EmitAsync(Notification notification, CancellationToken cancellationToken)
    {
        try
        {
            await _store.AddAsync(notification, cancellationToken);
            await _store.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Failed to emit {Kind} notification to recipient {RecipientId} for entity {EntityId}.",
                notification.Kind,
                notification.RecipientId,
                notification.EntityId);
        }
    }

    private static string FullName(User user) => $"{user.FirstName} {user.LastName}".Trim();

    private static string FormatRange(Booking booking)
    {
        var start = booking.StartDate.ToString("d MMM");
        var end = booking.EndDate.ToString("d MMM");
        return start == end ? start : $"{start}–{end}";
    }

    private static string BuildMeta(Booking booking, string? currency)
    {
        var days = booking.EndDate.DayNumber - booking.StartDate.DayNumber + 1;
        var dayLabel = days == 1 ? "day" : "days";
        var price = $"{booking.TotalPrice:0} {currency}".Trim();
        return $"{days} {dayLabel} · {price}";
    }

    private static string? PrimaryImageUrl(Listing listing) =>
        listing.Images?
            .OrderByDescending(image => image.IsPrimary)
            .ThenBy(image => image.SortOrder)
            .Select(image => image.Url)
            .FirstOrDefault();
}
