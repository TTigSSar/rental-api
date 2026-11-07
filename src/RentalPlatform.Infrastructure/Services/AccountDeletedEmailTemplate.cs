using System.Net;
using RentalPlatform.Application.Abstractions;

namespace RentalPlatform.Infrastructure.Services;

// "Your account was deleted" (ADR-056 section 4), en/hy/ru with an English fallback, same layout as the
// password-reset email. The body carries no user-controlled data - not a name, not the address - and no
// link, so it cannot be steered by anyone. It says what was erased and what stays, and points to the
// support address for the "this was not me" case.
public static class AccountDeletedEmailTemplate
{
    private const string SupportEmail = "support@dorent.am";

    private sealed record Copy(
        string Subject, string Preheader, string Heading, string Intro, string Erased, string Kept, string NotYou, string Footer);

    private static readonly Copy English = new(
        Subject: "Your DoRent account was deleted",
        Preheader: "Your personal data has been erased.",
        Heading: "Your account was deleted",
        Intro: "Your DoRent account has been deleted at your request. You are signed out everywhere.",
        Erased: "We erased your name, phone number, photo, home location and password, and archived your toys.",
        Kept: "Bookings, messages and reviews you took part in stay visible to the other people involved, shown as \"Deleted user\".",
        NotYou: $"If this was not you, write to {SupportEmail} as soon as possible.",
        Footer: "This is an automated message from DoRent. Please don’t reply to it.");

    private static readonly Copy Russian = new(
        Subject: "Ваш аккаунт DoRent удалён",
        Preheader: "Ваши персональные данные стёрты.",
        Heading: "Ваш аккаунт удалён",
        Intro: "Ваш аккаунт DoRent удалён по вашему запросу. Вы вышли из аккаунта на всех устройствах.",
        Erased: "Мы стёрли ваше имя, телефон, фото, домашнюю точку и пароль, а ваши игрушки отправили в архив.",
        Kept: "Бронирования, сообщения и отзывы, в которых вы участвовали, остаются у других участников и отображаются как «Удалённый пользователь».",
        NotYou: $"Если это были не вы, как можно скорее напишите на {SupportEmail}.",
        Footer: "Это автоматическое письмо от DoRent. Отвечать на него не нужно.");

    private static readonly Copy Armenian = new(
        Subject: "Ձեր DoRent հաշիվը ջնջվել է",
        Preheader: "Ձեր անձնական տվյալները ջնջվել են։",
        Heading: "Ձեր հաշիվը ջնջվել է",
        Intro: "Ձեր DoRent հաշիվը ջնջվել է Ձեր խնդրանքով։ Դուք դուրս եք եկել բոլոր սարքերից։",
        Erased: "Մենք ջնջել ենք Ձեր անունը, հեռախոսահամարը, լուսանկարը, տան վայրը և գաղտնաբառը, իսկ Ձեր խաղալիքները տեղափոխվել են արխիվ։",
        Kept: "Ամրագրումները, նամակագրությունը և կարծիքները, որոնց մասնակցել եք, մնում են մյուս մասնակիցների մոտ և ցուցադրվում են որպես «Ջնջված օգտատեր»։",
        NotYou: $"Եթե դա Դուք չեք եղել, հնարավորինս շուտ գրեք {SupportEmail} հասցեին։",
        Footer: "Սա DoRent-ի ավտոմատ նամակ է։ Խնդրում ենք չպատասխանել դրան։");

    private static Copy Select(string? preferredLanguage)
    {
        // "hy-AM" / "ru_RU" -> "hy" / "ru"; anything unknown or missing falls back to English.
        var code = (preferredLanguage ?? string.Empty).Trim().ToLowerInvariant();
        var separator = code.IndexOfAny(new[] { '-', '_' });
        if (separator > 0)
        {
            code = code[..separator];
        }

        return code switch
        {
            "hy" => Armenian,
            "ru" => Russian,
            _ => English
        };
    }

    public static EmailMessage Render(string to, string? preferredLanguage)
    {
        var copy = Select(preferredLanguage);

        var html =
            "<!DOCTYPE html><html><body style=\"margin:0;padding:24px;background:#f5f3f0;font-family:Arial,Helvetica,sans-serif;color:#1f2937;line-height:1.5;\">" +
            $"<div style=\"display:none;max-height:0;overflow:hidden;opacity:0;\">{WebUtility.HtmlEncode(copy.Preheader)}</div>" +
            "<div style=\"max-width:520px;margin:0 auto;background:#ffffff;border-radius:12px;padding:32px;\">" +
            $"<h2 style=\"margin:0 0 16px;\">{WebUtility.HtmlEncode(copy.Heading)}</h2>" +
            $"<p>{WebUtility.HtmlEncode(copy.Intro)}</p>" +
            $"<p>{WebUtility.HtmlEncode(copy.Erased)}</p>" +
            $"<p>{WebUtility.HtmlEncode(copy.Kept)}</p>" +
            "<hr style=\"border:0;border-top:1px solid #e5e7eb;margin:20px 0;\">" +
            $"<p style=\"color:#6b7280;font-size:13px;\">{WebUtility.HtmlEncode(copy.NotYou)}</p>" +
            "</div>" +
            $"<p style=\"max-width:520px;margin:16px auto 0;color:#9ca3af;font-size:12px;text-align:center;\">{WebUtility.HtmlEncode(copy.Footer)}<br>dorent.am</p>" +
            "</body></html>";

        var text =
            $"{copy.Heading}\n\n" +
            $"{copy.Intro}\n\n" +
            $"{copy.Erased}\n\n" +
            $"{copy.Kept}\n\n" +
            $"{copy.NotYou}\n\n" +
            $"{copy.Footer}\n";

        return new EmailMessage(to, copy.Subject, html, text);
    }
}
