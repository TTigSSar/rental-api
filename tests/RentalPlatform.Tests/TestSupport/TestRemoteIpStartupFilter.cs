using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace RentalPlatform.Tests.TestSupport;

// Test-host-only pipeline hook: lets an integration test pin the simulated client IP for its
// requests by sending the X-Test-Remote-Ip header, so anything keyed on
// HttpContext.Connection.RemoteIpAddress (e.g. the per-IP rate-limit partitions in
// RateLimiterExtensions) can be pointed at a value the test controls, instead of whatever the
// in-memory TestServer happens to report for every request in the run (which, empirically, is
// the SAME value for every client — the reason RateLimitingTests used to be order-dependent on
// other classes' traffic).
//
// Registered only by RentalPlatformWebAppFactory (see ConfigureTestServices) — no production
// code is touched, and requests that don't send the header are completely unaffected.
public sealed class TestRemoteIpStartupFilter : IStartupFilter
{
    public const string HeaderName = "X-Test-Remote-Ip";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(HeaderName, out var value) &&
                    IPAddress.TryParse(value.ToString(), out var simulatedIp))
                {
                    context.Connection.RemoteIpAddress = simulatedIp;
                }

                await nextMiddleware();
            });

            next(app);
        };
    }
}
