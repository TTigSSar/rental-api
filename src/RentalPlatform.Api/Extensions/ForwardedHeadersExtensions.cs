using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace RentalPlatform.Api.Extensions;

// Opt-in support for running behind a reverse proxy / load balancer. When the app sits behind
// a proxy, Connection.RemoteIpAddress is the proxy's IP, which would collapse every client into
// one per-IP rate-limit bucket and hide the real scheme from HTTPS-aware logic. Enabling this
// makes the framework honor X-Forwarded-For / X-Forwarded-Proto.
//
// It is OFF by default on purpose: blindly trusting forwarded headers while directly exposed
// lets a client spoof its IP. Enable it only when a trusted proxy actually sits in front, and
// scope trust to that proxy via configuration.
public static class ForwardedHeadersExtensions
{
    public const string SectionName = "ForwardedHeaders";

    public static IServiceCollection AddProxyForwardedHeaders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        if (!section.GetValue("Enabled", false))
        {
            return services;
        }

        var trustedProxies = section.GetSection("KnownProxies").Get<string[]>() ?? Array.Empty<string>();
        var trustedNetworks = section.GetSection("KnownNetworks").Get<string[]>() ?? Array.Empty<string>();
        var forwardLimit = section.GetValue<int?>("ForwardLimit");

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = forwardLimit;

            // Start from an empty trust list; only the explicitly configured proxies/networks are honored.
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();

            foreach (var proxy in trustedProxies)
            {
                if (IPAddress.TryParse(proxy.Trim(), out var address))
                {
                    options.KnownProxies.Add(address);
                }
            }

            foreach (var network in trustedNetworks)
            {
                if (TryParseCidr(network.Trim(), out var prefix, out var length))
                {
                    options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, length));
                }
            }
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
            !int.TryParse(parts[1], out var parsedLength))
        {
            return false;
        }

        prefix = parsedPrefix;
        prefixLength = parsedLength;
        return true;
    }
}
