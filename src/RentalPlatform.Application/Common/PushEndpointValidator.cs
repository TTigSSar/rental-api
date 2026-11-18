using System.Text;

namespace RentalPlatform.Application.Common;

/// <summary>
/// ADR-067 section 8: the push endpoint is a URL the server will POST to, so it is an SSRF vector. Only the
/// push services of the browsers we support are accepted; everything else is refused before it is stored and
/// again before it is used.
/// </summary>
public static class PushEndpointValidator
{
    public const int MaxEndpointLength = 2048;
    public const int P256dhLength = 65;
    public const int AuthLength = 16;

    private const string FcmHost = "fcm.googleapis.com";

    // Matched as a dot-boundary suffix ("*.push.apple.com"): "evilpush.apple.com" and the bare suffix do not match.
    private static readonly string[] AllowedSuffixes =
    {
        "push.services.mozilla.com",
        "notify.windows.com",
        "push.apple.com"
    };

    public static bool IsValidSubscription(string? endpoint, string? p256dh, string? auth) =>
        IsValidEndpoint(endpoint)
        && TryDecodeBase64Url(p256dh, out var key) && key.Length == P256dhLength
        && TryDecodeBase64Url(auth, out var secret) && secret.Length == AuthLength;

    public static bool IsValidEndpoint(string? endpoint)
    {
        if (string.IsNullOrEmpty(endpoint) || endpoint.Length > MaxEndpointLength)
        {
            return false;
        }

        // No whitespace or control characters anywhere: Uri would silently trim some of them.
        foreach (var character in endpoint)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                return false;
            }
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.IsDefaultPort
            || uri.HostNameType != UriHostNameType.Dns)
        {
            return false;
        }

        var host = uri.Host;
        if (host == FcmHost)
        {
            return true;
        }

        foreach (var suffix in AllowedSuffixes)
        {
            if (host.Length > suffix.Length + 1 && host.EndsWith("." + suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Strict base64url (RFC 4648 section 5), with or without padding.</summary>
    public static bool TryDecodeBase64Url(string? value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrEmpty(value) || value.Length > 256)
        {
            return false;
        }

        var end = value.Length;
        while (end > 0 && value[end - 1] == '=')
        {
            end--;
        }

        if (value.Length - end > 2)
        {
            return false;
        }

        var builder = new StringBuilder(end + 3);
        for (var index = 0; index < end; index++)
        {
            var character = value[index];
            if (character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(character);
            }
            else if (character == '-')
            {
                builder.Append('+');
            }
            else if (character == '_')
            {
                builder.Append('/');
            }
            else
            {
                return false;
            }
        }

        if (builder.Length % 4 == 1)
        {
            return false;
        }

        while (builder.Length % 4 != 0)
        {
            builder.Append('=');
        }

        try
        {
            bytes = Convert.FromBase64String(builder.ToString());
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
