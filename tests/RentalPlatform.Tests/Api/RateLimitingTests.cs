using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace RentalPlatform.Tests.Api;

// Verifies that rate-limiting policies reject requests once the permit window is exhausted.
// NOTE: Tests in this class must run AFTER all other integration-test classes in the
// collection so the auth-policy counter starts at zero.  xUnit discovers classes
// alphabetically; "R" sorts after "A", "B", "I" — this ordering is maintained by design.
[Collection("Integration")]
public sealed class RateLimitingTests
{
    private readonly RentalPlatformWebAppFactory _factory;

    public RateLimitingTests(RentalPlatformWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Auth_Login_Returns_429_After_PermitLimit_Exceeded()
    {
        // The auth policy allows 5 requests per minute (per-IP key "::1" or "unknown"
        // for the in-process TestServer). No other test class POSTs to login/register,
        // so the counter starts at zero when this test runs.
        var client = _factory.CreateClient();

        var permitLimit = 5; // matches RateLimiterExtensions.AuthPolicy PermitLimit
        HttpResponseMessage? lastResponse = null;

        for (var i = 0; i <= permitLimit; i++)
        {
            // Non-existent credentials → 401 from the auth service.  The rate limiter
            // runs first and still counts the request against the budget.
            lastResponse = await client.PostAsJsonAsync("/api/auth/login", new
            {
                email = "nonexistent@ratelimit-test.local",
                password = "Password123!"
            });
        }

        // The (permitLimit + 1)-th request must be rejected by the rate limiter.
        Assert.Equal(HttpStatusCode.TooManyRequests, lastResponse!.StatusCode);
    }
}
