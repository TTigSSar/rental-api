using System.Globalization;
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

    /// <summary>Rendered strings for one notification. <see cref="Meta"/> is null for kinds that carry none.</summary>
    public readonly record struct Rendered(string Title, string Body, string PrimaryActionLabel, string? Meta = null);

    // ---- Dates and amounts -------------------------------------------------------------------

    /// <summary>
    /// One culture per supported language, used only to spell the month. Rental dates are
    /// <see cref="DateOnly"/> calendar days, so no time zone is involved. Falls back to the
    /// invariant culture if the runtime has no ICU data for the culture (never throws).
    /// </summary>
    public static CultureInfo CultureFor(string language)
    {
        var name = language switch
        {
            "hy" => "hy-AM",
            "ru" => "ru-RU",
            _ => "en-US"
        };

        try
        {
            return CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    /// <summary>"5 Oct–7 Oct", or a single "5 Oct" when start equals end (matches the UI, ADR-052).</summary>
    public static string FormatRange(DateOnly start, DateOnly end, string language)
    {
        var culture = CultureFor(language);
        var from = start.ToString("d MMM", culture);
        var to = end.ToString("d MMM", culture);
        return from == to ? from : $"{from}–{to}";
    }

    /// <summary>"3 days · 1,500 ֏". The amount format is kept identical to the Angular DramCurrencyPipe.</summary>
    public static string BookingMeta(string language, int days, decimal totalPrice)
    {
        var amount = totalPrice.ToString("#,##0", CultureInfo.InvariantCulture);
        return $"{days} {DayLabel(language, days)} · {amount} ֏";
    }

    public static string DayLabel(string language, int days) => language switch
    {
        "hy" => "օր",
        "ru" => RussianDayForm(days),
        _ => days == 1 ? "day" : "days"
    };

    // 1 день / 2-4 дня / 5-20 дней; 11-14 are always "дней".
    private static string RussianDayForm(int days)
    {
        var abs = Math.Abs(days);
        var lastTwo = abs % 100;
        var last = abs % 10;
        if (lastTwo is >= 11 and <= 14) return "дней";
        return last switch
        {
            1 => "день",
            >= 2 and <= 4 => "дня",
            _ => "дней"
        };
    }

    // ---- Booking and listing notifications -----------------------------------------------------

    /// <summary>To the owner: a renter asked for their toy.</summary>
    public static Rendered BookingRequested(string language, string renterName, string toyTitle, string range, string meta) =>
        language switch
        {
            "hy" => new Rendered(
                $"Նոր վարձակալության հարցում՝ {renterName}",
                $"{renterName} ցանկանում է վարձակալել «{toyTitle}»՝ {range}։ Պատասխանեք 24 ժամվա ընթացքում՝ արագ պատասխանողի նշանը պահպանելու համար։",
                "Դիտել հարցումը", meta),
            "ru" => new Rendered(
                $"Новый запрос на аренду от {renterName}",
                $"{renterName} хочет взять «{toyTitle}» на {range}. Ответьте в течение 24 часов, чтобы сохранить значок быстрых ответов.",
                "Посмотреть запрос", meta),
            _ => new Rendered(
                $"New rental request from {renterName}",
                $"{renterName} wants your \"{toyTitle}\" for {range}. Respond within 24h to keep your fast-reply badge.",
                "Review request", meta)
        };

    /// <summary>To the renter: the owner approved.</summary>
    public static Rendered BookingApproved(string language, string ownerName, string toyTitle, string range, string meta) =>
        language switch
        {
            "hy" => new Rendered(
                "Ձեր հարցումը հաստատվել է",
                $"«{toyTitle}»-ը ձերն է՝ {range}։ Վերցնելու ժամը համաձայնեցրեք տիրոջ հետ՝ {ownerName}։",
                "Պայմանավորվել վերցնելու մասին", meta),
            "ru" => new Rendered(
                "Ваш запрос одобрен",
                $"Игрушка «{toyTitle}» ваша на {range}. Договоритесь с владельцем ({ownerName}) о времени получения.",
                "Договориться о передаче", meta),
            _ => new Rendered(
                $"{ownerName} approved your request",
                $"The \"{toyTitle}\" is yours for {range}. Arrange a pickup time with {ownerName}.",
                "Arrange pickup", meta)
        };

    /// <summary>To the renter: the owner declined. <paramref name="reason"/> is the owner's own free text, shown verbatim.</summary>
    public static Rendered BookingDeclined(string language, string ownerName, string toyTitle, string? reason, string meta)
    {
        var hasReason = !string.IsNullOrWhiteSpace(reason);
        return language switch
        {
            "hy" => new Rendered(
                "Ձեր հարցումը մերժվել է",
                hasReason
                    ? $"«{reason}» — ահա նմանատիպ խաղալիքներ մոտակայքում։"
                    : $"«{toyTitle}»-ի հարցումը մերժվել է։ Ահա նմանատիպ խաղալիքներ մոտակայքում։",
                "Գտնել նմանատիպ խաղալիքներ", meta),
            "ru" => new Rendered(
                "Ваш запрос отклонён",
                hasReason
                    ? $"«{reason}» — вот похожие игрушки поблизости."
                    : $"Запрос на «{toyTitle}» отклонён. Вот похожие игрушки поблизости.",
                "Найти похожие игрушки", meta),
            _ => new Rendered(
                $"{ownerName} declined your request",
                hasReason
                    ? $"\"{reason}\" — here are similar toys nearby."
                    : $"Your request for \"{toyTitle}\" was declined. Here are similar toys nearby.",
                "Find similar toys", meta)
        };
    }

    /// <summary>To the owner: the listing passed moderation.</summary>
    public static Rendered ListingLive(string language, string toyTitle) =>
        language switch
        {
            "hy" => new Rendered(
                "Ձեր հայտարարությունը հրապարակվել է",
                $"«{toyTitle}»-ը անցել է ստուգումը։ Այժմ ընտանիքները կարող են գտնել այն և ուղարկել հայտ։",
                "Դիտել հայտարարությունը"),
            "ru" => new Rendered(
                "Ваше объявление опубликовано",
                $"Объявление «{toyTitle}» прошло проверку. Теперь семьи могут найти его и отправить запрос.",
                "Открыть объявление"),
            _ => new Rendered(
                "Your listing is live",
                $"\"{toyTitle}\" passed moderation. Families can now find and request it.",
                "View public listing")
        };

    /// <summary>To the owner: moderation sent the listing back. <paramref name="reason"/> is the composed moderation text, shown verbatim.</summary>
    public static Rendered ListingChanges(string language, string toyTitle, string? reason)
    {
        var hasReason = !string.IsNullOrWhiteSpace(reason);
        return language switch
        {
            "hy" => new Rendered(
                "Հայտարարության մեջ փոփոխություններ են պետք",
                hasReason
                    ? $"{reason} Ուղղեք «{toyTitle}»-ի նշված թերությունները և նորից ուղարկեք։ Ուղղված հայտարարությունները ստուգվում են առանց հերթի։"
                    : $"«{toyTitle}»-ը պետք է ուղղել, մինչև այն հրապարակվի։ Ուղղեք և նորից ուղարկեք։",
                "Խմբագրել և նորից ուղարկել"),
            "ru" => new Rendered(
                "Нужны правки в объявлении",
                hasReason
                    ? $"{reason} Исправьте замечания в объявлении «{toyTitle}» и отправьте снова — исправленные объявления проверяются вне очереди."
                    : $"Объявление «{toyTitle}» нужно доработать перед публикацией. Исправьте замечания и отправьте снова.",
                "Изменить и отправить снова"),
            _ => new Rendered(
                "Changes needed on your listing",
                hasReason
                    ? $"{reason} Fix the issues on \"{toyTitle}\" and resubmit — fixes jump the review queue."
                    : $"\"{toyTitle}\" needs changes before it can go live. Fix the issues and resubmit.",
                "Edit & resubmit")
        };
    }

    private static string Fallback(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
