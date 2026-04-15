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

    private DateTimeOffset _appleJwksExpiresAt = DateTimeOffset.MinValue;
    private IReadOnlyCollection<SecurityKey> _appleJwksKeys = Array.Empty<SecurityKey>();
    private readonly SemaphoreSlim _appleJwksLock = new(1, 1);

    public ExternalIdentityTokenValidator(
        IOptions<ExternalAuthOptions> options,
        IHttpClientFactory httpClientFactory,
        TimeProvider timeProvider)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _timeProvider = timeProvider;
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
            _ => Failure("auth.external_provider_unsupported", $"Unsupported external provider '{provider}'.")
        };
    }

    private async Task<ServiceResult<ExternalUserInfo>> ValidateGoogleAsync(string idToken, CancellationToken cancellationToken)
    {
        if (_options.Google.ValidAudiences.Length == 0)
        {
            return Failure("auth.external_invalid_token", "Google external auth configuration is missing valid audiences.");
        }

        try
        {
            var payload = await GoogleJsonWebSignature.ValidateAsync(idToken, new GoogleJsonWebSignature.ValidationSettings
            {
                Audience = _options.Google.ValidAudiences
            });

            return ServiceResult<ExternalUserInfo>.Success(new ExternalUserInfo
            {
                Provider = "google",
                ProviderUserId = payload.Subject,
                Email = payload.Email,
                FirstName = payload.GivenName,
                LastName = payload.FamilyName,
                AvatarUrl = payload.Picture
            });
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure("auth.external_invalid_token", "Google identity token is invalid.");
        }
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

            return ServiceResult<ExternalUserInfo>.Success(new ExternalUserInfo
            {
                Provider = "apple",
                ProviderUserId = providerUserId,
                Email = principal.FindFirstValue(ClaimTypes.Email) ?? principal.FindFirstValue(JwtRegisteredClaimNames.Email),
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
