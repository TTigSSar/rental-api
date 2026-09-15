using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Common;

// Shared read/write translation between the additive multi-select DeliveryOptions ([Flags]) and
// the legacy scalar DeliveryType, used by both the create and update paths in
// ListingsOwnerService and the read projections in ListingsOwnerService.GetMineAsync and
// ListingsQueryService. Kept in one place so the two enums can never drift out of sync between
// write and read.
public static class DeliveryOptionsMapper
{
    // Order in which flags expand back into a DeliveryTypes list — stable and matches the enum's
    // declaration order (Pickup, Courier).
    private static readonly DeliveryType[] ExpansionOrder = { DeliveryType.Pickup, DeliveryType.Courier };

    // Combines a request's DeliveryTypes list and/or legacy DeliveryType field into the flags to
    // persist. DeliveryTypes wins when supplied (OR of its values); otherwise falls back to the
    // single legacy value; null when neither is supplied.
    public static DeliveryOptions? Combine(IReadOnlyList<DeliveryType>? deliveryTypes, DeliveryType? deliveryType)
    {
        if (deliveryTypes is not null)
        {
            var flags = DeliveryOptions.None;
            foreach (var value in deliveryTypes)
            {
                flags |= ToFlag(value);
            }

            return flags;
        }

        if (deliveryType is { } single)
        {
            return ToFlag(single);
        }

        return null;
    }

    // Legacy mirror: Pickup wins when both are offered (matches the "Pickup from me" default of
    // the old single-select wizard); Courier when only Courier is offered; null when the flags
    // are null/None.
    public static DeliveryType? ToLegacy(DeliveryOptions? flags)
    {
        if (flags is not { } value || value == DeliveryOptions.None)
        {
            return null;
        }

        return value.HasFlag(DeliveryOptions.Pickup) ? DeliveryType.Pickup : DeliveryType.Courier;
    }

    // Expands flags back into an ordered [Pickup, Courier] list for read responses. Falls back to
    // a single-element list built from the legacy DeliveryType when the flags themselves are
    // null/None but the legacy value exists (pre-migration rows); null when both are absent.
    public static IReadOnlyList<DeliveryType>? Expand(DeliveryOptions? flags, DeliveryType? legacyDeliveryType)
    {
        if (flags is { } value && value != DeliveryOptions.None)
        {
            var result = new List<DeliveryType>(ExpansionOrder.Length);
            foreach (var candidate in ExpansionOrder)
            {
                if (value.HasFlag(ToFlag(candidate)))
                {
                    result.Add(candidate);
                }
            }

            return result;
        }

        return legacyDeliveryType is { } legacy
            ? new List<DeliveryType> { legacy }
            : null;
    }

    // Update-path decision for a request that carries ONLY the legacy scalar DeliveryType
    // (DeliveryTypes is null). A pre-deploy client still on the old single-select edit form always
    // sends its one DeliveryType — typically the "Pickup from me" default — regardless of what the
    // listing actually offers. Blindly applying that value the way Combine does would silently drop
    // any other flag (e.g. Courier) the listing's current, richer DeliveryOptions already carries.
    // So: if the current flags already include the submitted legacy value, treat the request as a
    // no-op (the stale client's view is consistent with reality, nothing to change). Only apply the
    // legacy value — collapsing to that single flag, same as today — when it would be a real change,
    // e.g. a Courier-only listing edited by a client that only knows "Pickup".
    public static bool ShouldApplyLegacyOnlyUpdate(DeliveryOptions? currentOptions, DeliveryType legacyValue)
    {
        if (currentOptions is not { } current || current == DeliveryOptions.None)
        {
            return true;
        }

        return !current.HasFlag(ToFlag(legacyValue));
    }

    private static DeliveryOptions ToFlag(DeliveryType value) => value switch
    {
        DeliveryType.Pickup => DeliveryOptions.Pickup,
        DeliveryType.Courier => DeliveryOptions.Courier,
        _ => DeliveryOptions.None
    };
}
