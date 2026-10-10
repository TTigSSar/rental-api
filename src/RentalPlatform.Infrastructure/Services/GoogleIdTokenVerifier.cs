using Google.Apis.Auth;

namespace RentalPlatform.Infrastructure.Services;

/// <summary>
/// Seam over <see cref="GoogleJsonWebSignature.ValidateAsync(string, GoogleJsonWebSignature.ValidationSettings)"/>:
/// a Google-signed token cannot be produced offline, so the order of checks around it (signature,
/// then nonce, then email) is unit-tested against stub payloads instead (ADR-030 section 2).
/// </summary>
public interface IGoogleIdTokenVerifier
{
    /// <summary>
    /// Verifies signature, issuer, audience and expiry. Throws (an <see cref="InvalidJwtException"/>
    /// in the real implementation) when the token is not valid.
    /// </summary>
    Task<GoogleJsonWebSignature.Payload> VerifyAsync(
        string idToken, IReadOnlyList<string> audiences, CancellationToken cancellationToken = default);
}

public sealed class GoogleIdTokenVerifier : IGoogleIdTokenVerifier
{
    public Task<GoogleJsonWebSignature.Payload> VerifyAsync(
        string idToken, IReadOnlyList<string> audiences, CancellationToken cancellationToken = default) =>
        GoogleJsonWebSignature.ValidateAsync(idToken, new GoogleJsonWebSignature.ValidationSettings
        {
            Audience = audiences
        });
}
