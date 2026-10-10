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

    private static string Key48(string ip) => RateLimiterExtensions.ResolveClientKey48(IPAddress.Parse(ip));

    // The two Google sign-in policies key IPv6 on the /48 (ADR-030 section 7): one /48 holds 65 536
    // /64s, enough to fill the nonce store within minutes if each had its own budget.
    [Fact]
    public void Key48_IPv6_SameSlash48_SameKey_DifferentSlash48_DifferentKey()
    {
        Assert.Equal("2001:db8:1::/48", Key48("2001:db8:1:2:aaaa:bbbb:cccc:dddd"));
        Assert.Equal(Key48("2001:db8:1:2::1"), Key48("2001:db8:1:ffff:ffff::1")); // different /64, same /48
        Assert.NotEqual(Key48("2001:db8:1:2::1"), Key48("2001:db8:2:2::1"));
    }

    [Fact]
    public void Key48_IPv4_And_Mapped_IPv4_And_Null_Are_Unchanged()
    {
        Assert.Equal("203.0.113.9", Key48("203.0.113.9"));
        Assert.Equal("172.18.0.3", Key48("::ffff:172.18.0.3"));
        Assert.Equal("unknown", RateLimiterExtensions.ResolveClientKey48((IPAddress?)null));
    }

    [Fact]
    public void The_Slash64_Key_Used_By_Other_Policies_Is_Not_Affected_By_The_Slash48_Variant() =>
        Assert.Equal("2001:db8:1:2::/64", Key("2001:db8:1:2:aaaa:bbbb:cccc:dddd"));

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
