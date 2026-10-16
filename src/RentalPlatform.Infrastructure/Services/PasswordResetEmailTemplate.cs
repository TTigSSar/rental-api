using System.Net;
using RentalPlatform.Application.Abstractions;

namespace RentalPlatform.Infrastructure.Services;

// Password-reset email (ADR-035), en/hy/ru with an English fallback, same layout as the verification
// email. The body carries no user-controlled data - not a name, not the address - so the endpoint
// cannot be used as a phishing relay from the platform's own domain (ADR-028 section 12). The only
// variable part is the link, which the server built from configuration. Copy: Claude Design
// "Forgot Password" (em.* keys). The reset is never described as signing out other devices (ADR-021 §5).
public static class PasswordResetEmailTemplate
{
    private sealed record Copy(
        string Subject, string Preheader, string Heading, string Intro, string Action,
        string Valid, string LinkFallback, string Ignore, string Footer);

    private static readonly Copy English = new(
        Subject: "Reset your DoRent password",
        Preheader: "The link is valid for 30 minutes.",
        Heading: "Reset your password",
        Intro: "We received a request to reset the password for your DoRent account. Use the button below to set a new one.",
        Action: "Set a new password",
        Valid: "This link is valid for 30 minutes.",
        LinkFallback: "If the button doesn’t work, copy this link into your browser:",
        Ignore: "If you didn’t ask to reset your password, ignore this email. Your password won’t change.",
        Footer: "This is an automated message from DoRent. Please don’t reply to it.");

    private static readonly Copy Russian = new(
        Subject: "Сброс пароля DoRent",
        Preheader: "Ссылка действует 30 минут.",
        Heading: "Сброс пароля",
        Intro: "Мы получили запрос на сброс пароля для вашего аккаунта DoRent. Нажмите кнопку ниже, чтобы задать новый.",
        Action: "Задать новый пароль",
        Valid: "Ссылка действует 30 минут.",
        LinkFallback: "Если кнопка не работает, скопируйте эту ссылку в браузер:",
        Ignore: "Если вы не запрашивали сброс пароля, просто проигнорируйте это письмо. Пароль не изменится.",
        Footer: "Это автоматическое письмо от DoRent. Отвечать на него не нужно.");

    private static readonly Copy Armenian = new(
        Subject: "DoRent-ի գաղտնաբառի վերականգնում",
        Preheader: "Հղումը գործում է 30 րոպե։",
        Heading: "Գաղտնաբառի վերականգնում",
        Intro: "Մենք ստացել ենք ձեր DoRent հաշվի գաղտնաբառը վերականգնելու հայտ։ Նորը սահմանելու համար սեղմեք ստորև կոճակը։",
        Action: "Սահմանել նոր գաղտնաբառ",
        Valid: "Հղումը գործում է 30 րոպե։",
        LinkFallback: "Եթե կոճակը չի աշխատում, պատճենեք այս հղումը դիտարկիչում՝",
        Ignore: "Եթե դուք չեք խնդրել վերականգնել գաղտնաբառը, անտեսեք այս նամակը։ Ձեր գաղտնաբառը չի փոխվի։",
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

    public static EmailMessage Render(string to, string? preferredLanguage, string link)
    {
        var copy = Select(preferredLanguage);
        var encodedLink = WebUtility.HtmlEncode(link);

        var html =
            "<!DOCTYPE html><html><body style=\"margin:0;padding:24px;background:#f5f3f0;font-family:Arial,Helvetica,sans-serif;color:#1f2937;line-height:1.5;\">" +
            $"<div style=\"display:none;max-height:0;overflow:hidden;opacity:0;\">{WebUtility.HtmlEncode(copy.Preheader)}</div>" +
            "<div style=\"max-width:520px;margin:0 auto;background:#ffffff;border-radius:12px;padding:32px;\">" +
            $"<h2 style=\"margin:0 0 16px;\">{WebUtility.HtmlEncode(copy.Heading)}</h2>" +
            $"<p>{WebUtility.HtmlEncode(copy.Intro)}</p>" +
            $"<p><a href=\"{encodedLink}\" style=\"display:inline-block;padding:12px 24px;background:#c24600;color:#ffffff;" +
            $"font-size:15px;font-weight:700;text-decoration:none;border-radius:8px;\">{WebUtility.HtmlEncode(copy.Action)}</a></p>" +
            $"<p>{WebUtility.HtmlEncode(copy.Valid)}</p>" +
            "<hr style=\"border:0;border-top:1px solid #e5e7eb;margin:20px 0;\">" +
            $"<p style=\"color:#6b7280;font-size:13px;\">{WebUtility.HtmlEncode(copy.LinkFallback)}<br>{encodedLink}</p>" +
            "<hr style=\"border:0;border-top:1px solid #e5e7eb;margin:20px 0;\">" +
            $"<p style=\"color:#6b7280;font-size:13px;\">{WebUtility.HtmlEncode(copy.Ignore)}</p>" +
            "</div>" +
            $"<p style=\"max-width:520px;margin:16px auto 0;color:#9ca3af;font-size:12px;text-align:center;\">{WebUtility.HtmlEncode(copy.Footer)}<br>dorent.am</p>" +
            "</body></html>";

        var text =
            $"{copy.Heading}\n\n" +
            $"{copy.Intro}\n\n" +
            $"{link}\n\n" +
            $"{copy.Valid}\n" +
            $"{copy.Ignore}\n\n" +
            $"{copy.Footer}\n";

        return new EmailMessage(to, copy.Subject, html, text);
    }
}
