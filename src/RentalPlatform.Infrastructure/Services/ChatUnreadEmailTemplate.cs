using System.Net;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Infrastructure.Services;

// "New message in your chat" (ADR-059), en/hy/ru with an English fallback. Layout and copy follow
// knowledge/specs/chat-notifications/design/nt-pages.jsx (NEmail). Rendered at send time. It never carries
// message text (ADR-053): only who wrote (first name), which toy, and links.
public static class ChatUnreadEmailTemplate
{
    private sealed record Copy(
        Func<string, string, string> WroteHeading,
        Func<string, string> NewHeading,
        string DeletedUser,
        string Cta,
        string Why,
        string Unsubscribe,
        string Manage);

    private static readonly Copy English = new(
        WroteHeading: (name, toy) => $"{name} wrote to you about {toy}",
        NewHeading: toy => $"New in your chat about {toy}",
        DeletedUser: "Deleted user",
        Cta: "Open chat",
        Why: "You are getting this because you have a conversation on DoRent and the message has been unread for a while.",
        Unsubscribe: "Unsubscribe from message emails",
        Manage: "Manage in Settings");

    // The design's "написала вам" is gender-specific and the sender's gender is unknown, so the heading is
    // phrased without a past-tense verb.
    private static readonly Copy Russian = new(
        WroteHeading: (name, toy) => $"Сообщение от {name} об игрушке «{toy}»",
        NewHeading: toy => $"Новое в вашем чате об игрушке «{toy}»",
        DeletedUser: "Удалённый пользователь",
        Cta: "Открыть чат",
        Why: "Вы получили это письмо, потому что у вас есть переписка на DoRent и сообщение какое-то время остаётся непрочитанным.",
        Unsubscribe: "Отписаться от писем о сообщениях",
        Manage: "Настроить в DoRent");

    // The design appended "ն" to the name, which is wrong for names ending in a consonant, so the name
    // stays uninflected at the end of the sentence.
    private static readonly Copy Armenian = new(
        WroteHeading: (name, toy) => $"«{toy}» խաղալիքի մասին Ձեզ գրել է {name}",
        NewHeading: toy => $"Նոր բան Ձեր զրույցում՝ «{toy}» խաղալիքի մասին",
        DeletedUser: "Ջնջված օգտատեր",
        Cta: "Բացել զրույցը",
        Why: "Դուք ստանում եք այս նամակը, քանի որ DoRent-ում զրույց ունեք, և հաղորդագրությունը որոշ ժամանակ չի կարդացվել։",
        Unsubscribe: "Չստանալ նամակներ հաղորդագրությունների մասին",
        Manage: "Կառավարել Կարգավորումներում");

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

    /// <summary>The heading (also the subject): a human message names the sender, otherwise it is neutral.</summary>
    public static string Heading(ChatUnreadEmail email)
    {
        var copy = Select(email.PreferredLanguage);
        if (!email.HasHumanMessage)
        {
            return copy.NewHeading(email.ToyTitle);
        }

        var name = email.SenderDeleted || string.IsNullOrWhiteSpace(email.SenderFirstName)
            ? copy.DeletedUser
            : email.SenderFirstName.Trim();
        return copy.WroteHeading(name, email.ToyTitle);
    }

    public static EmailMessage Render(ChatUnreadEmail email)
    {
        var copy = Select(email.PreferredLanguage);
        var heading = Heading(email);

        const string border = "#E7E2D8";
        const string secondary = "#6b7280";

        var thumbnail = string.IsNullOrWhiteSpace(email.ToyImageUrl)
            ? $"<div style=\"width:64px;height:64px;border-radius:12px;background:#F2EFE8;\"></div>"
            : $"<img src=\"{WebUtility.HtmlEncode(email.ToyImageUrl)}\" width=\"64\" height=\"64\" alt=\"\" " +
              "style=\"display:block;width:64px;height:64px;object-fit:cover;border-radius:12px;border:0;\">";

        var html =
            "<!DOCTYPE html><html><body style=\"margin:0;padding:24px 0;background:#F2EFE8;font-family:Arial,Helvetica,sans-serif;color:#1f2937;line-height:1.5;\">" +
            $"<div style=\"max-width:600px;margin:0 auto;background:#ffffff;border-radius:16px;border:1px solid {border};overflow:hidden;\">" +
            $"<div style=\"padding:20px 32px;border-bottom:1px solid {border};font-size:28px;font-weight:700;color:#C24600;\">DoRent</div>" +
            "<div style=\"padding:32px;\">" +
            $"<h1 style=\"margin:0 0 20px;font-size:24px;line-height:32px;font-weight:700;color:#1f2937;\">{WebUtility.HtmlEncode(heading)}</h1>" +
            $"<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" style=\"width:100%;margin:0 0 20px;border:1px solid {border};border-radius:14px;\"><tr>" +
            $"<td style=\"padding:12px;width:64px;vertical-align:middle;\">{thumbnail}</td>" +
            $"<td style=\"padding:12px 12px 12px 2px;vertical-align:middle;font-size:16px;line-height:22px;font-weight:600;\">{WebUtility.HtmlEncode(email.ToyTitle)}</td>" +
            "</tr></table>" +
            $"<a href=\"{WebUtility.HtmlEncode(email.OpenChatUrl)}\" style=\"display:inline-block;min-height:24px;padding:12px 32px;border-radius:999px;" +
            "background:#C24600;color:#ffffff;font-size:16px;font-weight:600;line-height:24px;text-decoration:none;\">" +
            $"{WebUtility.HtmlEncode(copy.Cta)}</a>" +
            "</div>" +
            $"<div style=\"padding:20px 32px 24px;border-top:1px solid {border};background:#FAF8F4;font-size:13px;line-height:19px;color:{secondary};\">" +
            $"<p style=\"margin:0 0 10px;\">{WebUtility.HtmlEncode(copy.Why)}</p>" +
            $"<p style=\"margin:0;\"><a href=\"{WebUtility.HtmlEncode(email.UnsubscribeUrl)}\" style=\"color:{secondary};text-decoration:underline;margin-right:16px;\">{WebUtility.HtmlEncode(copy.Unsubscribe)}</a>" +
            $"<a href=\"{WebUtility.HtmlEncode(email.SettingsUrl)}\" style=\"color:{secondary};text-decoration:underline;\">{WebUtility.HtmlEncode(copy.Manage)}</a></p>" +
            "</div></div></body></html>";

        var text =
            $"{heading}\n\n" +
            $"{email.ToyTitle}\n\n" +
            $"{copy.Cta}: {email.OpenChatUrl}\n\n" +
            $"{copy.Why}\n\n" +
            $"{copy.Unsubscribe}: {email.UnsubscribeUrl}\n" +
            $"{copy.Manage}: {email.SettingsUrl}\n";

        return new EmailMessage(email.To, heading, html, text);
    }
}
