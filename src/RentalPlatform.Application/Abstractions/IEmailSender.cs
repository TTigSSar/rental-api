namespace RentalPlatform.Application.Abstractions;

public sealed record EmailMessage(string To, string Subject, string HtmlBody, string TextBody);

/// <summary>
/// Transport seam under <see cref="IEmailService"/> (ADR-029): content is decided above it, delivery
/// below it. Implementations MUST NOT throw — a failed send is logged and the caller carries on.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
