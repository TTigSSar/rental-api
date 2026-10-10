using System.Net;
using System.Net.Sockets;
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

    // verify-email and resend-verification (ADR-028 section 8). Per IP, one shared bucket for both
    // endpoints. Not a global limit: a global one would let a single attacker block everybody's
    // verification.
    public const string EmailVerificationPolicy = "email-verification";

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

            options.AddPolicy(EmailVerificationPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: ResolveClientKey(context),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
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

    // Partition key for per-IP policies. IPv4 (plain or IPv4-mapped IPv6) keys on the address;
    // any other IPv6 keys on its /64, because one subscriber holds a whole /64 and could otherwise
    // rotate through 2^64 addresses to dodge the limit (ADR-027 amendment).
    internal static string ResolveClientKey(HttpContext context) =>
        ResolveClientKey(context.Connection.RemoteIpAddress);

    internal static string ResolveClientKey(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out _))
        {
            return address.ToString();
        }

        bytes[8..].Clear();
        return new IPAddress(bytes).ToString() + "/64";
    }

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
