using System.Globalization;
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
    private const string SystemPlatformName = "DoRent";
    private const string SystemPlatformIcon = "heart";
    private const string SystemModeratorName = "DoRent Moderator";
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
            Meta = BuildMeta(booking),
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
            Meta = BuildMeta(booking),
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
            Meta = BuildMeta(booking),
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

    // The one emit site whose copy is rendered per-recipient rather than in English only: it is the
    // first notification a renter can receive about something the OWNER did to a booking already in
    // flight, and it names a place. Rendered at emit time in booking.Renter.PreferredLanguage (with
    // the English fallback) from NotificationCopy — see that file for why the language is frozen
    // here rather than resolved at read time.
    //
    // It is also the only emit site that does real work BEFORE handing a finished Notification to
    // EmitAsync — resolving a language, rendering copy, reading booking.Listing.Title. EmitAsync's
    // try/catch cannot cover any of that, so a booking whose Listing navigation is not loaded (or
    // whose row has since been deleted) threw straight out of this method, past HomePointService's
    // fan-out loop, and cost EVERY remaining renter their notification with nothing logged. The
    // whole body is therefore guarded here, which is what makes the callers' "the emitter logs its
    // own failures" contract actually true rather than aspirational.
    public async Task PickupAreaChangedAsync(Booking booking, User owner, District? newDistrict, CancellationToken cancellationToken = default)
    {
        Notification notification;
        try
        {
            var language = NotificationCopy.ResolveLanguage(booking.Renter?.PreferredLanguage);
            var districtName = newDistrict is null ? null : NotificationCopy.DistrictName(newDistrict, language);
            var copy = NotificationCopy.PickupAreaChanged(language, booking.Listing.Title, districtName);

            notification = new Notification
            {
                Id = Guid.NewGuid(),
                RecipientId = booking.RenterId,
                Kind = NotificationKind.Pickup,
                Category = NotificationCategory.Booking,
                Urgent = false,
                Title = copy.Title,
                Body = copy.Body,
                Meta = null,
                ActorName = FullName(owner),
                ActorAvatarUrl = owner.AvatarUrl,
                ActorVerified = owner.IsIdConfirmed,
                EntityType = NotificationEntityType.Booking,
                EntityId = booking.Id,
                // Deep-links to the booking, whose page hosts the chat thread for this rental — the
                // handover details the copy tells the renter to go and check.
                DeepLink = $"/bookings/{booking.Id}",
                ToyTitle = booking.Listing.Title,
                ToyImageUrl = PrimaryImageUrl(booking.Listing),
                PrimaryActionLabel = copy.PrimaryActionLabel,
                PrimaryActionDeepLink = $"/bookings/{booking.Id}",
                CreatedAt = DateTime.UtcNow
            };
        }
        catch (Exception exception)
        {
            // Logged, not rethrown: same contract as EmitAsync below. The identifiers come off the
            // booking row itself, so they are readable even when its navigations are not — which is
            // the failure this catch exists for, and the one detail that makes the log line
            // actionable (it names the booking to go and look at).
            _logger.LogError(
                exception,
                "Failed to build the {Kind} notification for booking {BookingId} (recipient {RecipientId}) — it was not sent.",
                NotificationKind.Pickup,
                booking.Id,
                booking.RenterId);
            return;
        }

        await EmitAsync(notification, cancellationToken);
    }

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

    /// <summary>
    /// DoRent is single-currency (AMD), so the ֏ symbol is a constant here rather than
    /// read from the listing/booking. This format is deliberately kept identical to
    /// DramCurrencyPipe in the Angular app (Rental-Ui/src/app/shared/utils/dram-currency.pipe.ts)
    /// — the two must be changed together.
    /// </summary>
    private static string BuildMeta(Booking booking)
    {
        var days = booking.EndDate.DayNumber - booking.StartDate.DayNumber + 1;
        var dayLabel = days == 1 ? "day" : "days";
        var amount = booking.TotalPrice.ToString("#,##0", CultureInfo.InvariantCulture);
        return $"{days} {dayLabel} · {amount}\u00A0֏";
    }

    private static string? PrimaryImageUrl(Listing listing) =>
        listing.Images?
            .OrderByDescending(image => image.IsPrimary)
            .ThenBy(image => image.SortOrder)
            .Select(image => image.Url)
            .FirstOrDefault();
}
