using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Api;

// Verifies the auth-policy rate limiter rejects requests exactly at its configured PermitLimit.
// Other integration-test classes in this collection also POST to /api/auth/login and
// /api/auth/me/password (a separate "password-change" policy, but still real HTTP calls), and
// the in-memory TestServer reports the same Connection.RemoteIpAddress for every request by
// default — so without isolation, every class's auth-policy traffic lands in the same per-IP
// bucket and this test's result would depend on how much of that bucket earlier classes had
// already spent (fragile under reordering, and unable to catch a limit that was simply
// misconfigured, since the assertion would pass for almost any limit depending on what was left).
//
// Instead, every request in this class is tagged with a remote IP unique to this test run (via
// TestRemoteIpStartupFilter, wired into the test host only — see RentalPlatformWebAppFactory), so
// this class's bucket starts empty regardless of what ran before it, and no other class's traffic
// can land in it either. That makes the transition point below an exact, order-independent
// invariant: exactly PermitLimit requests succeed (i.e. are not 429), and the next one is 429.
[Collection("Integration")]
public sealed class RateLimitingTests
{
    private static int _ipCounter;

    private readonly RentalPlatformWebAppFactory _factory;

    public RateLimitingTests(RentalPlatformWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Auth_Login_Returns_429_After_PermitLimit_Exceeded()
    {
        const int permitLimit = 5; // matches RateLimiterExtensions.AuthPolicy PermitLimit

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpStartupFilter.HeaderName, NextTestIp());

        for (var i = 0; i < permitLimit; i++)
        {
            // Non-existent credentials → 401 from the auth service. The rate limiter runs first
            // and still counts the request against the budget. Every one of these first
            // `permitLimit` requests must be let through the limiter (not 429) — if this fails,
            // the effective limit is lower than PermitLimit.
            var response = await client.PostAsJsonAsync("/api/auth/login", new
            {
                email = "nonexistent@ratelimit-test.local",
                password = "Password123!"
            });

            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        // The (permitLimit + 1)-th request must be rejected — if this fails, the effective limit
        // is higher than PermitLimit.
        var overLimitResponse = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email = "nonexistent@ratelimit-test.local",
            password = "Password123!"
        });

        Assert.Equal(HttpStatusCode.TooManyRequests, overLimitResponse.StatusCode);
    }

    // The home-point policy is partitioned per ACCOUNT, not per IP, because moving a home point
    // rewrites every listing the owner has and can notify every renter with a booking in flight.
    // So this test does NOT need an IP tag to be isolated — a fresh user id is its own bucket — and
    // proving that is half the point: two different users must not share a budget.
    [Fact]
    public async Task HomePoint_Put_Returns_429_After_PermitLimit_Exceeded_Per_User()
    {
        const int permitLimit = 10; // matches RateLimiterExtensions.HomePointPolicy PermitLimit

        var userId = Guid.NewGuid();
        var email = $"{userId:N}@rate-limit.local";
        await _factory.SeedAsync(TestData.User(userId, email));

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestJwtTokenHelper.GenerateToken(userId, email));

        // Each request nudges the point so none of them is the unchanged-point no-op — the limiter
        // runs before the service either way, but this keeps the calls representative.
        for (var i = 0; i < permitLimit; i++)
        {
            var response = await client.PutAsJsonAsync("/api/auth/me/home-point", new
            {
                latitude = TestData.KentronPoint.Latitude + (i * 0.0001m),
                longitude = TestData.KentronPoint.Longitude
            });

            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        var overLimitResponse = await client.PutAsJsonAsync("/api/auth/me/home-point", new
        {
            latitude = TestData.KentronPoint.Latitude,
            longitude = TestData.KentronPoint.Longitude
        });

        Assert.Equal(HttpStatusCode.TooManyRequests, overLimitResponse.StatusCode);

        // A different account, same IP, same moment: unaffected. If the partition were per IP, a
        // household behind one NAT address would lock each other out.
        var otherUserId = Guid.NewGuid();
        var otherEmail = $"{otherUserId:N}@rate-limit.local";
        await _factory.SeedAsync(TestData.User(otherUserId, otherEmail));

        var otherClient = _factory.CreateClient();
        otherClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestJwtTokenHelper.GenerateToken(otherUserId, otherEmail));

        var otherResponse = await otherClient.PutAsJsonAsync("/api/auth/me/home-point", new
        {
            latitude = TestData.KentronPoint.Latitude,
            longitude = TestData.KentronPoint.Longitude
        });

        Assert.Equal(HttpStatusCode.OK, otherResponse.StatusCode);
    }

    // A simulated private-range IPv4 address unique within this test run, so repeated runs of
    // this test method (or future sibling tests in this class) never share a rate-limit bucket.
    private static string NextTestIp()
    {
        var n = Interlocked.Increment(ref _ipCounter);
        return $"10.77.{(n >> 8) & 0xFF}.{n & 0xFF}";
    }
}
