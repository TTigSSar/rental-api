using System.Net;
using Microsoft.Extensions.Logging;
using RentalPlatform.Application.Abstractions;

namespace RentalPlatform.Infrastructure.Services;

/// <summary>
/// Email content (ADR-029): decides WHICH email goes out and in which language, and hands the
/// rendered message to the <see cref="IEmailSender"/> transport. Only the verification email is
/// delivered through the transport; the listing approved/rejected emails stay on the logging path
/// until a separate decision enables them (ADR-029 D5), exactly as they behaved before.
/// </summary>
public sealed class EmailService : IEmailService
{
    private readonly IEmailSender _sender;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IEmailSender sender, ILogger<EmailService> logger)
    {
        _sender = sender;
        _logger = logger;
    }

    public Task SendListingApprovedAsync(
        string ownerEmail,
        string ownerName,
        string listingTitle,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[DEV-EMAIL] TO: {Email} | SUBJECT: Your toy listing has been approved\n" +
            "Hi {Name},\n\n" +
            "Great news! Your listing \"{Title}\" has been approved and is now publicly visible on the platform.\n\n" +
            "Parents can now find and book your toy. Thank you for listing with us!\n\n" +
            "— Child Toys Rental Team",
            ownerEmail, ownerName, listingTitle);

        return Task.CompletedTask;
    }

    public Task SendListingRejectedAsync(
        string ownerEmail,
        string ownerName,
        string listingTitle,
        string rejectionReason,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[DEV-EMAIL] TO: {Email} | SUBJECT: Your toy listing was not approved\n" +
            "Hi {Name},\n\n" +
            "Unfortunately your listing \"{Title}\" was not approved.\n\n" +
            "Reason: {Reason}\n\n" +
            "If you have questions or would like to make changes and resubmit, please contact support.\n\n" +
            "— Child Toys Rental Team",
            ownerEmail, ownerName, listingTitle, rejectionReason);

        return Task.CompletedTask;
    }

    public async Task SendEmailVerificationAsync(
        string email,
        string? preferredLanguage,
        string link,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var message = VerificationEmailTemplate.Render(email, preferredLanguage, link);
            await _sender.SendAsync(message, cancellationToken);
        }
        catch (Exception exception)
        {
            // IEmailService never throws: the account is already committed and the user can resend.
            _logger.LogError(exception, "Verification email could not be sent ({ExceptionType}).", exception.GetType().Name);
        }
    }
}

// en/hy/ru with an English fallback (ADR-025 precedent), HTML plus a text part. The body carries no
// user-controlled data - not even the name - so register cannot be used as an open phishing relay
// from the platform's own domain (ADR-028 section 12). The only variable part is the link, which
// the server built from configuration.
public static class VerificationEmailTemplate
{
    private sealed record Copy(string Subject, string Heading, string Intro, string Action, string Expiry, string Ignore, string LinkFallback);

    private static readonly Copy English = new(
        Subject: "Confirm your email on DoRent",
        Heading: "Confirm your email",
        Intro: "Thanks for signing up to DoRent. To finish creating your account, open the link below and enter your password to confirm your email address.",
        Action: "Confirm email",
        Expiry: "The link is valid for 24 hours.",
        Ignore: "If you did not sign up, you can safely ignore this email.",
        LinkFallback: "If the button does not work, copy this link into your browser:");

    private static readonly Copy Russian = new(
        Subject: "Подтвердите email в DoRent",
        Heading: "Подтвердите email",
        Intro: "Спасибо за регистрацию в DoRent. Чтобы завершить создание аккаунта, откройте ссылку ниже и введите пароль, чтобы подтвердить адрес электронной почты.",
        Action: "Подтвердить email",
        Expiry: "Ссылка действует 24 часа.",
        Ignore: "Если вы не регистрировались, просто проигнорируйте это письмо.",
        LinkFallback: "Если кнопка не работает, скопируйте эту ссылку в браузер:");

    private static readonly Copy Armenian = new(
        Subject: "Հաստատեք Ձեր էլ. փոստը DoRent-ում",
        Heading: "Հաստատեք Ձեր էլ. փոստը",
        Intro: "Շնորհակալություն DoRent-ում գրանցվելու համար։ Հաշիվը ստեղծելն ավարտելու համար բացեք ստորև նշված հղումը և մուտքագրեք Ձեր գաղտնաբառը՝ էլ. փոստի հասցեն հաստատելու համար։",
        Action: "Հաստատել էլ. փոստը",
        Expiry: "Հղումը գործում է 24 ժամ։",
        Ignore: "Եթե Դուք չեք գրանցվել, պարզապես անտեսեք այս նամակը։",
        LinkFallback: "Եթե կոճակը չի աշխատում, պատճենեք այս հղումը զննարկիչ՝");

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
            "<!DOCTYPE html><html><body style=\"font-family:Arial,Helvetica,sans-serif;color:#1f2937;line-height:1.5;\">" +
            $"<h2 style=\"margin:0 0 16px;\">{WebUtility.HtmlEncode(copy.Heading)}</h2>" +
            $"<p>{WebUtility.HtmlEncode(copy.Intro)}</p>" +
            $"<p><a href=\"{encodedLink}\" style=\"display:inline-block;padding:12px 24px;background:#2563eb;color:#ffffff;" +
            $"text-decoration:none;border-radius:6px;\">{WebUtility.HtmlEncode(copy.Action)}</a></p>" +
            $"<p>{WebUtility.HtmlEncode(copy.Expiry)}</p>" +
            $"<p style=\"color:#6b7280;font-size:13px;\">{WebUtility.HtmlEncode(copy.LinkFallback)}<br>{encodedLink}</p>" +
            $"<p style=\"color:#6b7280;font-size:13px;\">{WebUtility.HtmlEncode(copy.Ignore)}</p>" +
            "</body></html>";

        var text =
            $"{copy.Heading}\n\n" +
            $"{copy.Intro}\n\n" +
            $"{link}\n\n" +
            $"{copy.Expiry}\n" +
            $"{copy.Ignore}\n";

        return new EmailMessage(to, copy.Subject, html, text);
    }
}
