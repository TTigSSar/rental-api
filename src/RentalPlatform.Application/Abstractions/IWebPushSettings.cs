namespace RentalPlatform.Application.Abstractions;

/// <summary>VAPID configuration as the application layer needs it (ADR-067 section 14).</summary>
public interface IWebPushSettings
{
    /// <summary>True when the public key, the private key and the subject are all configured.</summary>
    bool IsEnabled { get; }

    /// <summary>The application-server public key (base64url), or null when push is not configured.</summary>
    string? PublicKey { get; }
}
