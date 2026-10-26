using Microsoft.Extensions.Logging;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Domain;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Infrastructure.Services;

/// <summary>
/// Builds and persists notification rows for domain events. Every method is
/// best-effort: failures are logged and swallowed so a notification problem can
/// never break the booking/moderation action that triggered it.
/// </summary>
/// <remarks>
/// Every kind is rendered at emit time in the RECIPIENT's PreferredLanguage (English fallback) from
/// <see cref="NotificationCopy"/>, per the ADR-025 amendment. The recipient's User row must therefore
/// be loaded on the entity graph handed in: Request and the listing kinds -> listing.Owner,
/// Approved/Declined/Pickup -> booking.Renter. A missing navigation falls back to English.
/// </remarks>
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
        EmitAsync(NotificationKind.Request, booking.Id, listing.OwnerId, () =>
        {
            var language = NotificationCopy.ResolveLanguage(listing.Owner?.PreferredLanguage);
            var copy = NotificationCopy.BookingRequested(
                language, renter.FirstName, listing.Title, Range(booking, language), Meta(booking, language));
            return new Notification
            {
                Id = Guid.NewGuid(),
                RecipientId = listing.OwnerId,
                Kind = NotificationKind.Request,
                Category = NotificationCategory.Booking,
                Urgent = true,
                Title = copy.Title,
                Body = copy.Body,
                Meta = copy.Meta,
                ActorName = FullName(renter),
                ActorAvatarUrl = renter.AvatarUrl,
                ActorVerified = renter.IsIdConfirmed,
                EntityType = NotificationEntityType.Booking,
                EntityId = booking.Id,
                DeepLink = "/bookings/requests",
                ToyTitle = listing.Title,
                ToyImageUrl = PrimaryImageUrl(listing),
                PrimaryActionLabel = copy.PrimaryActionLabel,
                PrimaryActionDeepLink = "/bookings/requests",
                CreatedAt = DateTime.UtcNow
            };
        }, cancellationToken);

    public Task BookingApprovedAsync(Booking booking, User owner, CancellationToken cancellationToken = default) =>
        EmitAsync(NotificationKind.Approved, booking.Id, booking.RenterId, () =>
        {
            var language = NotificationCopy.ResolveLanguage(booking.Renter?.PreferredLanguage);
            var copy = NotificationCopy.BookingApproved(
                language, owner.FirstName, booking.Listing.Title, Range(booking, language), Meta(booking, language));
            return new Notification
            {
                Id = Guid.NewGuid(),
                RecipientId = booking.RenterId,
                Kind = NotificationKind.Approved,
                Category = NotificationCategory.Booking,
                Urgent = false,
                Title = copy.Title,
                Body = copy.Body,
                Meta = copy.Meta,
                ActorName = FullName(owner),
                ActorAvatarUrl = owner.AvatarUrl,
                ActorVerified = owner.IsIdConfirmed,
                EntityType = NotificationEntityType.Booking,
                EntityId = booking.Id,
                DeepLink = $"/bookings/{booking.Id}",
                ToyTitle = booking.Listing.Title,
                ToyImageUrl = PrimaryImageUrl(booking.Listing),
                PrimaryActionLabel = copy.PrimaryActionLabel,
                PrimaryActionDeepLink = $"/bookings/{booking.Id}",
                CreatedAt = DateTime.UtcNow
            };
        }, cancellationToken);

    public Task BookingDeclinedAsync(Booking booking, User owner, CancellationToken cancellationToken = default) =>
        EmitAsync(NotificationKind.Declined, booking.Id, booking.RenterId, () =>
        {
            var language = NotificationCopy.ResolveLanguage(booking.Renter?.PreferredLanguage);
            var copy = NotificationCopy.BookingDeclined(
                language, owner.FirstName, booking.Listing.Title, booking.RejectionReason, Meta(booking, language));
            return new Notification
            {
                Id = Guid.NewGuid(),
                RecipientId = booking.RenterId,
                Kind = NotificationKind.Declined,
                Category = NotificationCategory.Booking,
                Urgent = false,
                Title = copy.Title,
                Body = copy.Body,
                Meta = copy.Meta,
                ActorName = FullName(owner),
                ActorAvatarUrl = owner.AvatarUrl,
                ActorVerified = owner.IsIdConfirmed,
                EntityType = NotificationEntityType.Booking,
                EntityId = booking.Id,
                DeepLink = "/listings",
                ToyTitle = booking.Listing.Title,
                ToyImageUrl = PrimaryImageUrl(booking.Listing),
                PrimaryActionLabel = copy.PrimaryActionLabel,
                PrimaryActionDeepLink = "/listings",
                CreatedAt = DateTime.UtcNow
            };
        }, cancellationToken);

    public Task ListingApprovedAsync(Listing listing, CancellationToken cancellationToken = default) =>
        EmitAsync(NotificationKind.ListingLive, listing.Id, listing.OwnerId, () =>
        {
            var language = NotificationCopy.ResolveLanguage(listing.Owner?.PreferredLanguage);
            var copy = NotificationCopy.ListingLive(language, listing.Title);
            return new Notification
            {
                Id = Guid.NewGuid(),
                RecipientId = listing.OwnerId,
                Kind = NotificationKind.ListingLive,
                Category = NotificationCategory.Listing,
                Urgent = false,
                Title = copy.Title,
                Body = copy.Body,
                Meta = null,
                ActorName = SystemPlatformName,
                ActorIsSystem = true,
                ActorSystemIcon = SystemPlatformIcon,
                EntityType = NotificationEntityType.Listing,
                EntityId = listing.Id,
                DeepLink = $"/listings/{listing.Id}",
                ToyTitle = listing.Title,
                ToyImageUrl = PrimaryImageUrl(listing),
                PrimaryActionLabel = copy.PrimaryActionLabel,
                PrimaryActionDeepLink = $"/listings/{listing.Id}",
                CreatedAt = DateTime.UtcNow
            };
        }, cancellationToken);

    public Task ListingRejectedAsync(Listing listing, string? reasonCode, string? note, CancellationToken cancellationToken = default) =>
        EmitAsync(NotificationKind.ListingChanges, listing.Id, listing.OwnerId, () =>
        {
            var language = NotificationCopy.ResolveLanguage(listing.Owner?.PreferredLanguage);
            var reason = string.IsNullOrWhiteSpace(reasonCode)
                ? note
                : RejectionReasonCatalog.Compose(reasonCode, note, language);
            var copy = NotificationCopy.ListingChanges(language, listing.Title, reason);
            return new Notification
            {
                Id = Guid.NewGuid(),
                RecipientId = listing.OwnerId,
                Kind = NotificationKind.ListingChanges,
                Category = NotificationCategory.Listing,
                Urgent = true,
                Title = copy.Title,
                Body = copy.Body,
                Meta = null,
                ActorName = SystemModeratorName,
                ActorIsSystem = true,
                ActorSystemIcon = SystemModeratorIcon,
                EntityType = NotificationEntityType.Listing,
                EntityId = listing.Id,
                DeepLink = $"/my-listings/{listing.Id}/edit",
                ToyTitle = listing.Title,
                ToyImageUrl = PrimaryImageUrl(listing),
                PrimaryActionLabel = copy.PrimaryActionLabel,
                PrimaryActionDeepLink = $"/my-listings/{listing.Id}/edit",
                CreatedAt = DateTime.UtcNow
            };
        }, cancellationToken);

    // Rendered per-recipient in booking.Renter.PreferredLanguage (with the English fallback) from
    // NotificationCopy — see that file for why the language is frozen here rather than resolved at
    // read time.
    //
    // The copy is built inside EmitAsync's guard (see the builder overload): rendering touches
    // navigations that may be unloaded (a booking whose Listing is missing used to throw straight out
    // of this method, past HomePointService's fan-out loop, and cost EVERY remaining renter their
    // notification with nothing logged). The callers' "the emitter logs its own failures" contract
    // depends on that.
    public Task PickupAreaChangedAsync(Booking booking, User owner, District? newDistrict, CancellationToken cancellationToken = default) =>
        EmitAsync(NotificationKind.Pickup, booking.Id, booking.RenterId, () =>
        {
            var language = NotificationCopy.ResolveLanguage(booking.Renter?.PreferredLanguage);
            var districtName = newDistrict is null ? null : NotificationCopy.DistrictName(newDistrict, language);
            var copy = NotificationCopy.PickupAreaChanged(language, booking.Listing.Title, districtName);

            return new Notification
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
        }, cancellationToken);

    // Builds inside the guard: a throw while rendering is logged, not propagated to the
    // booking/moderation action that triggered the notification.
    private async Task EmitAsync(
        NotificationKind kind, Guid entityId, Guid recipientId, Func<Notification> build, CancellationToken cancellationToken)
    {
        Notification notification;
        try
        {
            notification = build();
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Failed to build the {Kind} notification for entity {EntityId} (recipient {RecipientId}) — it was not sent.",
                kind,
                entityId,
                recipientId);
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

    private static string Range(Booking booking, string language) =>
        NotificationCopy.FormatRange(booking.StartDate, booking.EndDate, language);

    /// <summary>
    /// DoRent is single-currency (AMD), so the ֏ symbol is a constant here rather than
    /// read from the listing/booking. The amount format (see <see cref="NotificationCopy.BookingMeta"/>)
    /// is deliberately kept identical to DramCurrencyPipe in the Angular app
    /// (Rental-Ui/src/app/shared/utils/dram-currency.pipe.ts) — the two must be changed together.
    /// </summary>
    private static string Meta(Booking booking, string language) =>
        NotificationCopy.BookingMeta(language, RentalPeriod.Days(booking.StartDate, booking.EndDate), booking.TotalPrice);

    private static string? PrimaryImageUrl(Listing listing) =>
        listing.Images?
            .OrderByDescending(image => image.IsPrimary)
            .ThenBy(image => image.SortOrder)
            .Select(image => image.Url)
            .FirstOrDefault();
}
