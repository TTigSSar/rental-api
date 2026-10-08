using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Api;

// Proves the ForwardedHeaders wiring end to end through the real middleware pipeline, using the
// auth rate limiter (5 req/min per resolved client IP) as the observable: whichever IP the app
// believes the client has is the bucket the request lands in.
//
// Each test derives its own host via WithWebHostBuilder so the ForwardedHeaders config is
// per-test and never leaks into the shared host (and thus into other classes' rate-limit buckets).
// The simulated proxy/source address is pinned with X-Test-Remote-Ip (TestRemoteIpStartupFilter).
// All IPs are unique per test.
[Collection("Integration")]
public sealed class ForwardedHeadersTests
{
    private const int AuthPermitLimit = 5;
    private const string ProxyMapped = "::ffff:172.30.0.3"; // dual-stack Kestrel shows IPv4 as mapped

    private static int _counter;

    private readonly RentalPlatformWebAppFactory _factory;

    public ForwardedHeadersTests(RentalPlatformWebAppFactory factory) => _factory = factory;

    private HttpClient CreateClient(Dictionary<string, string?> config) =>
        _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(config)))
            .CreateClient();

    private static Dictionary<string, string?> TrustedNetworkConfig(string? forwardLimit = "1") => new()
    {
        ["ForwardedHeaders:Enabled"] = "true",
        ["ForwardedHeaders:KnownNetworks:0"] = "172.30.0.0/24",
        ["ForwardedHeaders:ForwardLimit"] = forwardLimit,
    };

    // Unique per call, in 198.18.0.0/15 (benchmarking range) so it never collides with other
    // tests' ranges (RateLimitingTests uses 10.77.x.y) or with the trusted 172.30.0.0/24.
    private static string NextIp()
    {
        var n = Interlocked.Increment(ref _counter);
        return $"198.18.{(n >> 8) & 0xFF}.{n & 0xFF}";
    }

    // Unique address inside the trusted 172.30.0.0/24 (.10-.254). Used as a rightmost XFF hop that
    // is itself trusted, and (mapped) as a distinct proxy address per test.
    private static int _trustedCounter = 9;

    private static string NextTrustedIp() => $"172.30.0.{Interlocked.Increment(ref _trustedCounter)}";

    private static async Task<HttpStatusCode> LoginAsync(HttpClient client, string remoteIp, string? xff)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email = "nobody@forwarded-headers.local", password = "Password123!" })
        };
        request.Headers.Add(TestRemoteIpStartupFilter.HeaderName, remoteIp);
        if (xff is not null) request.Headers.Add("X-Forwarded-For", xff);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static async Task ExhaustAsync(HttpClient client, string remoteIp, Func<int, string?> xff)
    {
        for (var i = 0; i < AuthPermitLimit; i++)
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, await LoginAsync(client, remoteIp, xff(i)));
        }
    }

    [Fact]
    public async Task TrustedProxy_MappedIPv4_UsesForwardedClientIp_ForRateLimitBucket()
    {
        var client = CreateClient(TrustedNetworkConfig());
        var clientA = NextIp();
        var clientB = NextIp();

        await ExhaustAsync(client, ProxyMapped, _ => clientA);

        // A is spent, even though the proxy address is the same for everyone.
        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginAsync(client, ProxyMapped, clientA));
        // B, through the same proxy, has its own bucket.
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await LoginAsync(client, ProxyMapped, clientB));
    }

    [Fact]
    public async Task UntrustedSource_ForgedForwardedFor_IsIgnored_AndSharesSourceBucket()
    {
        var client = CreateClient(TrustedNetworkConfig());
        var source = NextIp();

        // Every request forges a different client IP; they must all land in the source's bucket.
        await ExhaustAsync(client, source, _ => NextIp());

        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginAsync(client, source, NextIp()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    public async Task Disabled_OrUnparsableEnabled_StartsUp_AndIgnoresForwardedFor(string enabled)
    {
        var client = CreateClient(new()
        {
            ["ForwardedHeaders:Enabled"] = enabled,
            ["ForwardedHeaders:KnownNetworks:0"] = "172.30.0.0/24",
            ["ForwardedHeaders:ForwardLimit"] = "",
        });
        // Source is inside the (configured but inactive) trusted range, so only "disabled" can
        // explain XFF being ignored: varying forged values still share the proxy's bucket.
        var proxy = "::ffff:" + NextTrustedIp();

        await ExhaustAsync(client, proxy, _ => NextIp());
        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginAsync(client, proxy, NextIp()));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("172.30.0.0/33")]
    [InlineData("")]
    public async Task Enabled_WithNoValidTrustEntry_FailsClosed_AndIgnoresForwardedFor(string network)
    {
        var client = CreateClient(new()
        {
            ["ForwardedHeaders:Enabled"] = "true",
            ["ForwardedHeaders:KnownNetworks:0"] = network,
        });
        var source = NextIp();

        // With empty trust lists the middleware would trust every peer; we must stay off instead.
        await ExhaustAsync(client, source, _ => NextIp());
        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginAsync(client, source, NextIp()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    public async Task EmptyOrGarbageForwardLimit_StartsUp_AndDefaultsToOne(string forwardLimit)
    {
        var client = CreateClient(TrustedNetworkConfig(forwardLimit));
        var proxy = "::ffff:" + NextTrustedIp();
        var trustedHop = NextTrustedIp();

        // Rightmost hop is itself trusted: with limit 1 the client is that hop; with an unlimited
        // limit the middleware would keep walking left and the (varying) leftmost value would win.
        await ExhaustAsync(client, proxy, _ => $"{NextIp()}, {trustedHop}");

        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginAsync(client, proxy, $"{NextIp()}, {trustedHop}"));
    }

    // ADR-027: only X-Forwarded-For is honored. Cloudflare's X-Forwarded-Proto passes through nginx
    // untouched, so honoring it would flip Request.Scheme to https in prod. A test-only startup
    // filter reports the scheme the app sees at response start (after UseForwardedHeaders ran).
    [Fact]
    public async Task TrustedProxy_XForwardedProto_IsIgnored_SchemeStaysHttp()
    {
        var client = _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(TrustedNetworkConfig()));
            b.ConfigureTestServices(s => s.AddSingleton<IStartupFilter, SchemeProbeStartupFilter>());
        }).CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email = "nobody@forwarded-headers.local", password = "Password123!" })
        };
        request.Headers.Add(TestRemoteIpStartupFilter.HeaderName, ProxyMapped);
        request.Headers.Add("X-Forwarded-For", NextIp());
        request.Headers.Add("X-Forwarded-Proto", "https");
        using var response = await client.SendAsync(request);

        Assert.Equal("http", Assert.Single(response.Headers.GetValues(SchemeProbeStartupFilter.HeaderName)));
    }

    private sealed class SchemeProbeStartupFilter : IStartupFilter
    {
        public const string HeaderName = "X-Test-Scheme";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers[HeaderName] = context.Request.Scheme;
                    return Task.CompletedTask;
                });
                await nextMiddleware();
            });
            next(app);
        };
    }

    [Fact]
    public async Task TrustedProxy_MultiValueForwardedFor_ForwardLimitOne_UsesRightmostValue()
    {
        var client = CreateClient(TrustedNetworkConfig());
        var proxy = "::ffff:" + NextTrustedIp();
        var rightmost = NextIp();
        var otherRightmost = NextIp();
        var trustedHop = NextTrustedIp();

        // Leftmost value (attacker-controlled) changes every request; the bucket is keyed by the
        // rightmost one, so the 6th request is still limited.
        await ExhaustAsync(client, proxy, _ => $"{NextIp()}, {rightmost}");
        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginAsync(client, proxy, $"{NextIp()}, {rightmost}"));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await LoginAsync(client, proxy, $"{NextIp()}, {otherRightmost}"));

        // A trusted rightmost hop is NOT walked past with ForwardLimit=1.
        var proxy2 = "::ffff:" + NextTrustedIp();
        await ExhaustAsync(client, proxy2, _ => $"{NextIp()}, {trustedHop}");
        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginAsync(client, proxy2, $"{NextIp()}, {trustedHop}"));
    }
}
