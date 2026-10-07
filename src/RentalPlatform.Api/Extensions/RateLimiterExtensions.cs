using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
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
    public const string PasswordChangePolicy = "password-change";

    // Moving a home point rewrites every listing the owner has and can fan out notifications to
    // every renter with a booking in flight, so it is partitioned per ACCOUNT rather than per IP —
    // an attacker on many IPs still gets one budget per victim account, and a household behind one
    // NAT address does not share a budget.
    public const string HomePointPolicy = "home-point";

    // The district-under-the-pin lookup. Anonymous by design (sign-up needs it before an account
    // exists), so per IP is the only partition available. Generous enough for a map being dragged
    // (the client debounces), tight enough that it is not a free point-in-polygon service.
    public const string DistrictLookupPolicy = "district-lookup";

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

            options.AddPolicy(PasswordChangePolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: ResolveClientKey(context),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));

            options.AddPolicy(HomePointPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: ResolveUserKey(context),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromHours(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));

            options.AddPolicy(DistrictLookupPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: ResolveClientKey(context),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));
        });

        return services;
    }

    private static string ResolveClientKey(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    // Per-account partition for authenticated endpoints. The IP fallback only applies to a request
    // with no usable user id — which the [Authorize] filter rejects anyway, so it exists purely so
    // an unauthenticated request can never land in another account's partition.
    private static string ResolveUserKey(HttpContext context)
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

        return string.IsNullOrWhiteSpace(userId) ? $"ip:{ResolveClientKey(context)}" : $"user:{userId}";
    }
}
