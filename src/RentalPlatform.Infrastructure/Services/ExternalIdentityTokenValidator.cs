using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using Google.Apis.Auth;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Infrastructure.Services;

public sealed class ExternalIdentityTokenValidator : IExternalIdentityTokenValidator
{
    private static readonly TimeSpan AppleJwksCacheDuration = TimeSpan.FromHours(6);

    private readonly ExternalAuthOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IGoogleIdTokenVerifier _googleVerifier;
    private readonly ExternalAuthNonceStore _nonceStore;

    private DateTimeOffset _appleJwksExpiresAt = DateTimeOffset.MinValue;
    private IReadOnlyCollection<SecurityKey> _appleJwksKeys = Array.Empty<SecurityKey>();
    private readonly SemaphoreSlim _appleJwksLock = new(1, 1);

    public ExternalIdentityTokenValidator(
        IOptions<ExternalAuthOptions> options,
        IHttpClientFactory httpClientFactory,
        TimeProvider timeProvider,
        IGoogleIdTokenVerifier googleVerifier,
        ExternalAuthNonceStore nonceStore)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _timeProvider = timeProvider;
        _googleVerifier = googleVerifier;
        _nonceStore = nonceStore;
    }

    public async Task<ServiceResult<ExternalUserInfo>> ValidateAsync(
        string provider,
        string idToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return Failure("auth.external_provider_unsupported", "External provider is required.");
        }

        if (string.IsNullOrWhiteSpace(idToken))
        {
            return Failure("auth.external_invalid_token", "Identity token is required.");
        }

        return provider.Trim().ToLowerInvariant() switch
        {
            "google" => await ValidateGoogleAsync(idToken, cancellationToken),
            "apple" => await ValidateAppleAsync(idToken, cancellationToken),
            _ => Failure("auth.external_provider_unsupported", "Unsupported external provider.")
        };
    }

    // Order is load-bearing (ADR-030 section 2): signature/iss/aud/exp first, so unsigned garbage
    // never touches the nonce store; then the nonce read FROM THE TOKEN is consumed atomically;
    // only then is anything inside the token (email, names) looked at. The nonce stays consumed even
    // when a later step answers 403 or 409.
    private async Task<ServiceResult<ExternalUserInfo>> ValidateGoogleAsync(string idToken, CancellationToken cancellationToken)
    {
        var audiences = _options.Google.ConfiguredAudiences;
        if (audiences.Length == 0)
        {
            return Failure("auth.external_provider_unavailable", "Google sign-in is not available.");
        }

        GoogleJsonWebSignature.Payload payload;
        try
        {
            payload = await _googleVerifier.VerifyAsync(idToken, audiences, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure("auth.external_invalid_token", "Google identity token is invalid.");
        }

        if (!_nonceStore.TryConsume(payload.Nonce, _timeProvider.GetUtcNow()))
        {
            return Failure("auth.external_invalid_token", "Google identity token is invalid.");
        }

        // Only trust the email for account creation/linking if Google says it is verified.
        // An unverified email must not be used to claim or link an existing account.
        var verifiedEmail = payload.EmailVerified == true ? payload.Email : null;

        return ServiceResult<ExternalUserInfo>.Success(new ExternalUserInfo
        {
            Provider = "google",
            ProviderUserId = payload.Subject,
            Email = verifiedEmail,
            FirstName = payload.GivenName,
            LastName = payload.FamilyName,
            FullName = payload.Name,
            AvatarUrl = payload.Picture,
            HostedDomain = payload.HostedDomain
        });
    }

    private async Task<ServiceResult<ExternalUserInfo>> ValidateAppleAsync(string idToken, CancellationToken cancellationToken)
    {
        if (_options.Apple.ValidAudiences.Length == 0)
        {
            return Failure("auth.external_invalid_token", "Apple external auth configuration is missing valid audiences.");
        }

        try
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var keys = await GetAppleSigningKeysAsync(cancellationToken);

            var principal = tokenHandler.ValidateToken(idToken, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = keys,
                ValidateIssuer = true,
                ValidIssuer = _options.Apple.Issuer,
                ValidateAudience = true,
                ValidAudiences = _options.Apple.ValidAudiences,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1)
            }, out _);

            var providerUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ??
                                 principal.FindFirstValue(JwtRegisteredClaimNames.Sub);

            if (string.IsNullOrWhiteSpace(providerUserId))
            {
                return Failure("auth.external_invalid_token", "Apple identity token is invalid.");
            }

            // Same rule as Google above: an email is used for account creation, linking or
            // replacing a pending registration only when the provider vouches for it (ADR-028 section 10).
            var appleEmail = principal.FindFirstValue(ClaimTypes.Email) ?? principal.FindFirstValue(JwtRegisteredClaimNames.Email);
            var appleEmailVerified = IsAppleEmailVerified(principal.FindFirstValue("email_verified"));

            return ServiceResult<ExternalUserInfo>.Success(new ExternalUserInfo
            {
                Provider = "apple",
                ProviderUserId = providerUserId,
                Email = appleEmailVerified ? appleEmail : null,
                FirstName = principal.FindFirstValue("given_name"),
                LastName = principal.FindFirstValue("family_name"),
                AvatarUrl = null
            });
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure("auth.external_invalid_token", "Apple identity token is invalid.");
        }
    }

    // Apple sends email_verified as a JSON bool or, historically, as the string "true". Anything
    // else (missing, "false", garbage) means the email is not vouched for.
    internal static bool IsAppleEmailVerified(string? claimValue) =>
        bool.TryParse(claimValue?.Trim(), out var verified) && verified;

    private async Task<IReadOnlyCollection<SecurityKey>> GetAppleSigningKeysAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (now < _appleJwksExpiresAt && _appleJwksKeys.Count > 0)
        {
            return _appleJwksKeys;
        }

        await _appleJwksLock.WaitAsync(cancellationToken);
        try
        {
            now = _timeProvider.GetUtcNow();
            if (now < _appleJwksExpiresAt && _appleJwksKeys.Count > 0)
            {
                return _appleJwksKeys;
            }

            var client = _httpClientFactory.CreateClient(nameof(ExternalIdentityTokenValidator));
            using var response = await client.GetAsync(_options.Apple.JwksUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            var jwks = await response.Content.ReadFromJsonAsync<AppleJwksResponse>(cancellationToken: cancellationToken);
            if (jwks?.Keys is null || jwks.Keys.Length == 0)
            {
                throw new InvalidOperationException("Apple JWKS response did not contain any keys.");
            }

            _appleJwksKeys = jwks.Keys.Select(key => (SecurityKey)new JsonWebKey(key.ToJson())).ToArray();
            _appleJwksExpiresAt = now.Add(AppleJwksCacheDuration);
            return _appleJwksKeys;
        }
        finally
        {
            _appleJwksLock.Release();
        }
    }

    private static ServiceResult<ExternalUserInfo> Failure(string code, string message) =>
        ServiceResult<ExternalUserInfo>.Failure(new ServiceError
        {
            Code = code,
            Message = message
        });

    private sealed class AppleJwksResponse
    {
        public AppleJwk[] Keys { get; init; } = Array.Empty<AppleJwk>();
    }

    private sealed class AppleJwk
    {
        public string Kty { get; init; } = string.Empty;
        public string Kid { get; init; } = string.Empty;
        public string Use { get; init; } = string.Empty;
        public string Alg { get; init; } = string.Empty;
        public string N { get; init; } = string.Empty;
        public string E { get; init; } = string.Empty;

        public string ToJson() =>
            $$"""
              {"kty":"{{Kty}}","kid":"{{Kid}}","use":"{{Use}}","alg":"{{Alg}}","n":"{{N}}","e":"{{E}}"}
              """;
    }
}
