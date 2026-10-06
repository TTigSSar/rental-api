using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Services;

/// <summary>
/// Server-side notification copy, per language.
/// <para>
/// Notification rows store rendered strings, not translation keys — see the "copy produced
/// server-side" contract documented on <see cref="Notification"/>. That contract is what lets the
/// feed render with no joins and system senders look uniform, and it is deliberately kept.
/// </para>
/// <para>
/// The consequence is that a notification's language is fixed at EMIT time, from the recipient's
/// <see cref="User.PreferredLanguage"/>, with English as the fallback for null/unknown values. A
/// user who later switches language keeps the notifications they already received in the language
/// they were in when they arrived; only new ones follow the new setting. That is a known, accepted
/// trade-off of the server-rendered contract, not an oversight.
/// </para>
/// <para>
/// This table exists so the translations live in one reviewable place instead of being interpolated
/// inline at each emit site. Only the notifications that need more than one language are here.
/// </para>
/// </summary>
internal static class NotificationCopy
{
    public const string DefaultLanguage = "en";

    private static readonly string[] SupportedLanguages = ["en", "hy", "ru"];

    /// <summary>
    /// Normalises a user's PreferredLanguage to one of the three supported tokens. Null, blank,
    /// unknown values and regional variants ("ru-RU") all resolve to something sane; anything not
    /// recognised falls back to English.
    /// </summary>
    public static string ResolveLanguage(string? preferredLanguage)
    {
        if (string.IsNullOrWhiteSpace(preferredLanguage))
        {
            return DefaultLanguage;
        }

        var primary = preferredLanguage.Trim().Split('-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(primary))
        {
            return DefaultLanguage;
        }

        return SupportedLanguages.FirstOrDefault(
            language => string.Equals(language, primary, StringComparison.OrdinalIgnoreCase))
            ?? DefaultLanguage;
    }

    /// <summary>The district's own name in the recipient's language (the District row carries all three).</summary>
    public static string DistrictName(District district, string language) => language switch
    {
        "hy" => Fallback(district.NameHy, district.NameEn),
        "ru" => Fallback(district.NameRu, district.NameEn),
        _ => district.NameEn
    };

    /// <summary>
    /// "The owner of &lt;toy&gt; moved their pickup area" — emitted to the renter of every in-flight
    /// booking when an owner moves their home point. <paramref name="districtName"/> is null when
    /// the new point falls outside every known Yerevan district, which is a legal state (the owner
    /// may genuinely have moved out of Yerevan), so it gets its own wording rather than an empty
    /// gap in the sentence.
    /// </summary>
    public static PickupAreaChangedCopy PickupAreaChanged(string language, string toyTitle, string? districtName) =>
        language switch
        {
            "hy" => new PickupAreaChangedCopy(
                Title: "Վերցնելու վայրը փոխվել է",
                Body: districtName is null
                    ? $"«{toyTitle}»-ի տերը տեղափոխվել է Երևանից դուրս։ Ճշտեք հանձնման մանրամասները զրույցում։"
                    : $"«{toyTitle}»-ի տերը տեղափոխվել է {districtName}։ Ճշտեք հանձնման մանրամասները զրույցում։",
                PrimaryActionLabel: "Դիտել ամրագրումը"),

            "ru" => new PickupAreaChangedCopy(
                Title: "Место выдачи изменилось",
                Body: districtName is null
                    ? $"Владелец «{toyTitle}» переехал за пределы Еревана. Уточните детали передачи в чате."
                    : $"Владелец «{toyTitle}» переехал в район {districtName}. Уточните детали передачи в чате.",
                PrimaryActionLabel: "Открыть бронирование"),

            _ => new PickupAreaChangedCopy(
                Title: "Pickup area changed",
                Body: districtName is null
                    ? $"The owner of \"{toyTitle}\" moved outside Yerevan. Check the chat for handover details."
                    : $"The owner of \"{toyTitle}\" moved to {districtName}. Check the chat for handover details.",
                PrimaryActionLabel: "View booking")
        };

    public readonly record struct PickupAreaChangedCopy(string Title, string Body, string PrimaryActionLabel);

    private static string Fallback(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
