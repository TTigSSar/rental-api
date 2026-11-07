using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using RentalPlatform.Application.Abstractions;

namespace RentalPlatform.Infrastructure.Services;

// ADR-059 amendment: stateless unsubscribe token. Layout (before base64url): userId (16 bytes) |
// expiry as Unix seconds (8 bytes, big endian) | HMAC-SHA256 over those 24 bytes (32 bytes). The MAC key is
// derived from the JWT signing secret with HKDF and a purpose label, so a token is never a valid JWT
// signature input and the JWT key itself is never used to sign anything else directly.
public sealed class ChatEmailUnsubscribeTokens : IChatEmailUnsubscribeTokens
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(60);

    private const string PurposeLabel = "chat-email-unsubscribe.v1";
    private const int PayloadLength = 16 + 8;
    private const int MacLength = 32;

    private readonly byte[] _key;
    private readonly TimeProvider _timeProvider;

    public ChatEmailUnsubscribeTokens(IOptions<JwtOptions> jwtOptions, TimeProvider timeProvider)
    {
        _key = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            Encoding.UTF8.GetBytes(jwtOptions.Value.SecretKey),
            outputLength: 32,
            salt: null,
            info: Encoding.UTF8.GetBytes(PurposeLabel));
        _timeProvider = timeProvider;
    }

    public string Issue(Guid userId)
    {
        var expiresAt = _timeProvider.GetUtcNow().Add(Lifetime).ToUnixTimeSeconds();

        var buffer = new byte[PayloadLength + MacLength];
        userId.TryWriteBytes(buffer.AsSpan(0, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(16, 8), expiresAt);
        HMACSHA256.HashData(_key, buffer.AsSpan(0, PayloadLength), buffer.AsSpan(PayloadLength, MacLength));

        return Base64UrlEncode(buffer);
    }

    public Guid? Validate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128)
        {
            return null;
        }

        var bytes = Base64UrlDecode(token.Trim());
        if (bytes is null || bytes.Length != PayloadLength + MacLength)
        {
            return null;
        }

        var expected = HMACSHA256.HashData(_key, bytes.AsSpan(0, PayloadLength));
        if (!CryptographicOperations.FixedTimeEquals(expected, bytes.AsSpan(PayloadLength, MacLength)))
        {
            return null;
        }

        var expiresAt = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(16, 8));
        if (_timeProvider.GetUtcNow().ToUnixTimeSeconds() >= expiresAt)
        {
            return null;
        }

        return new Guid(bytes.AsSpan(0, 16), bigEndian: true);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[]? Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            0 => padded,
            _ => string.Empty
        };

        if (padded.Length == 0)
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
