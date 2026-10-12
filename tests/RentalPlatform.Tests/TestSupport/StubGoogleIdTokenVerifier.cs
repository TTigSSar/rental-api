using Google.Apis.Auth;
using RentalPlatform.Infrastructure.Services;

namespace RentalPlatform.Tests.TestSupport;

// A Google-signed token cannot be produced offline, so tests register the payload a "token" string
// stands for. An unknown token fails exactly like a bad signature would (InvalidJwtException).
public sealed class StubGoogleIdTokenVerifier : IGoogleIdTokenVerifier
{
    public Dictionary<string, GoogleJsonWebSignature.Payload> Tokens { get; } = new();
    public int Calls { get; private set; }
    public IReadOnlyList<string>? LastAudiences { get; private set; }

    public Task<GoogleJsonWebSignature.Payload> VerifyAsync(
        string idToken, IReadOnlyList<string> audiences, CancellationToken cancellationToken = default)
    {
        Calls++;
        LastAudiences = audiences;
        return Tokens.TryGetValue(idToken, out var payload)
            ? Task.FromResult(payload)
            : throw new InvalidJwtException("bad signature");
    }

    public string Register(string token, string? nonce, string subject = "google-sub-1", string email = "someone@gmail.com",
        bool emailVerified = true, string? givenName = "Gina", string? familyName = "Google", string? name = "Gina Google",
        string? hostedDomain = null)
    {
        Tokens[token] = new GoogleJsonWebSignature.Payload
        {
            Subject = subject,
            Email = email,
            EmailVerified = emailVerified,
            GivenName = givenName,
            FamilyName = familyName,
            Name = name,
            HostedDomain = hostedDomain,
            Picture = "https://example.org/p.png",
            Nonce = nonce
        };
        return token;
    }
}
