namespace RentalPlatform.Application.Abstractions;

/// <summary>
/// Read-only view of the email configuration the verification flow depends on (ADR-028 §9).
/// </summary>
public interface IEmailVerificationSettings
{
    /// <summary>
    /// False in Production when email cannot actually be delivered (provider is not Resend, the API
    /// key is empty, or the public base URL is not https). Register and resend answer 503 and create
    /// nothing while this is false.
    /// </summary>
    bool IsOperational { get; }

    /// <summary>Builds <c>{App:PublicBaseUrl}/auth/verify-email#token=…</c>. Never derived from the request host.</summary>
    string BuildVerificationLink(string token);
}

/// <summary>
/// Process-wide cap on emails actually sent, sized to the provider quota (ADR-028 §8). Counts sends,
/// not requests, so one caller can never make registration fail for everyone else.
/// </summary>
public interface IEmailSendBudget
{
    /// <summary>Reserves one send. False when the window is exhausted (a Critical log is written).</summary>
    bool TryAcquire();
}
