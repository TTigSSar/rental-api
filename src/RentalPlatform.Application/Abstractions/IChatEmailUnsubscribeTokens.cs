namespace RentalPlatform.Application.Abstractions;

/// <summary>
/// Stateless signed token for the "unsubscribe from message emails" link (ADR-059 amendment): HMAC over the
/// user id and an expiry, valid 60 days, no table.
/// </summary>
public interface IChatEmailUnsubscribeTokens
{
    /// <summary>URL-safe token for <paramref name="userId"/>, expiring 60 days after now.</summary>
    string Issue(Guid userId);

    /// <summary>The user id when the token is genuine and unexpired; null for anything else.</summary>
    Guid? Validate(string? token);
}
