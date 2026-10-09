using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace RentalPlatform.Api.Extensions;

// Opt-in support for running behind a reverse proxy / load balancer. When the app sits behind
// a proxy, Connection.RemoteIpAddress is the proxy's IP, which would collapse every client into
// one per-IP rate-limit bucket. Enabling this makes the framework honor X-Forwarded-For only
// (ADR-027; X-Forwarded-Proto is deliberately NOT honored).
//
// Fails closed: with no valid KnownProxies/KnownNetworks entry, forwarding stays off.
//
// It is OFF by default on purpose: blindly trusting forwarded headers while directly exposed
// lets a client spoof its IP. Enable it only when a trusted proxy actually sits in front, and
// scope trust to that proxy via configuration.
public static class ForwardedHeadersExtensions
{
    public const string SectionName = "ForwardedHeaders";

    public static IServiceCollection AddProxyForwardedHeaders(this IServiceCollection services)
    {
        // Bound lazily (when the options are first resolved) so the final host configuration is
        // what counts. Parsing is tolerant on purpose (M-016): compose renders an unset variable as
        // an empty string, and a throwing parse here would take the whole site down at startup.
        services.AddOptions<ForwardedHeadersOptions>().Configure<IConfiguration, ILoggerFactory>((options, config, loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("RentalPlatform.Api.ForwardedHeaders");
            var section = config.GetSection(SectionName);
            if (!bool.TryParse(section["Enabled"]?.Trim(), out var enabled) || !enabled)
            {
                return;
            }

            var trustedProxies = section.GetSection("KnownProxies").Get<string[]>() ?? Array.Empty<string>();
            var trustedNetworks = section.GetSection("KnownNetworks").Get<string[]>() ?? Array.Empty<string>();
            int? forwardLimit = int.TryParse(section["ForwardLimit"]?.Trim(), out var limit) && limit > 0
                ? limit
                : 1;

            options.ForwardLimit = forwardLimit;

            // Start from an empty trust list; only the explicitly configured proxies/networks are honored.
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();

            foreach (var proxy in trustedProxies)
            {
                if (IPAddress.TryParse(proxy?.Trim(), out var address))
                {
                    options.KnownProxies.Add(address);
                }
                else
                {
                    logger.LogWarning("ForwardedHeaders: ignoring unparsable KnownProxies entry '{Entry}'.", proxy);
                }
            }

            foreach (var network in trustedNetworks)
            {
                if (TryParseCidr(network?.Trim() ?? string.Empty, out var prefix, out var length))
                {
                    options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, length));
                }
                else
                {
                    logger.LogWarning("ForwardedHeaders: ignoring unparsable KnownNetworks entry '{Entry}'.", network);
                }
            }

            // Fail closed: the middleware trusts EVERY peer when both lists are empty, so with no
            // usable trust entry forwarding must stay off (ADR-027, rejected option d).
            if (options.KnownProxies.Count == 0 && options.KnownNetworks.Count == 0)
            {
                logger.LogWarning(
                    "ForwardedHeaders is enabled but no valid KnownProxies/KnownNetworks entry parsed; forwarded headers are DISABLED.");
                options.ForwardedHeaders = ForwardedHeaders.None;
                return;
            }

            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor; // ADR-027: X-Forwarded-Proto is deliberately not honored
        });

        return services;
    }

    private static bool TryParseCidr(string value, out IPAddress prefix, out int prefixLength)
    {
        prefix = IPAddress.None;
        prefixLength = 0;

        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 ||
            !IPAddress.TryParse(parts[0], out var parsedPrefix) ||
            !int.TryParse(parts[1], out var parsedLength) ||
            parsedLength < 0 ||
            parsedLength > (parsedPrefix.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128))
        {
            return false;
        }

        prefix = parsedPrefix;
        prefixLength = parsedLength;
        return true;
    }
}
