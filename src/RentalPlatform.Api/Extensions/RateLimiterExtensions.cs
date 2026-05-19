using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace RentalPlatform.Api.Extensions;

// Fixed-window per-IP rate limit policies for sensitive write endpoints.
// Conservative defaults sized for an MVP single-instance deployment — adjust
// when the platform grows past a single node (then switch to a distributed limiter).
public static class RateLimiterExtensions
{
    public const string AuthPolicy = "auth";
    public const string BookingCreatePolicy = "booking-create";
    public const string ImageUploadPolicy = "image-upload";

    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(AuthPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: ResolveClientKey(context),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));

            options.AddPolicy(BookingCreatePolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: ResolveClientKey(context),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));

            options.AddPolicy(ImageUploadPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: ResolveClientKey(context),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));
        });

        return services;
    }

    private static string ResolveClientKey(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
