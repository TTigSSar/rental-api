using System.Net;
using System.Net.Http.Json;
using RentalPlatform.Api.Extensions;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Api;

// Rate-limit partition key (ADR-027 amendment): IPv4 and IPv4-mapped IPv6 key on the address,
// any other IPv6 on its /64.
[Collection("Integration")]
public sealed class ClientKeyTests
{
    private readonly RentalPlatformWebAppFactory _factory;

    public ClientKeyTests(RentalPlatformWebAppFactory factory) => _factory = factory;

    private static string Key(string ip) => RateLimiterExtensions.ResolveClientKey(IPAddress.Parse(ip));

    [Fact]
    public void Null_IsUnknown() => Assert.Equal("unknown", RateLimiterExtensions.ResolveClientKey((IPAddress?)null));

    [Fact]
    public void PlainIPv4_KeysOnAddress() => Assert.Equal("203.0.113.9", Key("203.0.113.9"));

    [Fact]
    public void MappedIPv4_EqualsPlainIPv4()
    {
        Assert.Equal(Key("172.18.0.3"), Key("::ffff:172.18.0.3"));
        Assert.Equal("172.18.0.3", Key("::ffff:172.18.0.3"));
    }

    [Fact]
    public void IPv6_SameSlash64_SameKey()
    {
        Assert.Equal(Key("2001:db8:1:2::1"), Key("2001:db8:1:2:ffff:ffff:ffff:ffff"));
        Assert.Equal("2001:db8:1:2::/64", Key("2001:db8:1:2:aaaa:bbbb:cccc:dddd"));
    }

    [Fact]
    public void IPv6_DifferentSlash64_DifferentKey() =>
        Assert.NotEqual(Key("2001:db8:1:2::1"), Key("2001:db8:1:3::1"));

    [Fact]
    public async Task IPv6_SameSlash64_ShareAuthBucket_DifferentSlash64_DoesNot()
    {
        var client = _factory.CreateClient();

        async Task<HttpStatusCode> LoginFrom(string ip)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
            {
                Content = JsonContent.Create(new { email = "nobody@client-key.local", password = "Password123!" })
            };
            request.Headers.Add(TestRemoteIpStartupFilter.HeaderName, ip);
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }

        // 5 requests from five different addresses of 2001:db8:a001:1::/64 spend one shared budget.
        for (var i = 1; i <= 5; i++)
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, await LoginFrom($"2001:db8:a001:1::{i:x}"));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginFrom("2001:db8:a001:1:dead:beef:0:1"));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await LoginFrom("2001:db8:a001:2::1"));
    }
}
