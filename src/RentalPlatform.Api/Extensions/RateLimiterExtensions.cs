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

    // Google sign-in (ADR-030 section 7). Two policies, because one attempt costs about three
    // requests (a nonce on open, the sign-in, a nonce after the answer) and a shared 10/min bucket
    // would allow roughly three attempts a minute for a whole carrier-grade-NAT address. Both are
    // per IP, with IPv6 keyed by /48 (see ResolveClientKey48).
    public const string ExternalAuthNoncePolicy = "external-auth-nonce";
    public const string ExternalAuthPolicy = "external-auth";

    // Name and phone edits, per ACCOUNT (same reasoning as HomePointPolicy), 10 per hour each.
    public const string NameUpdatePolicy = "name-update";
    public const string PhoneUpdatePolicy = "phone-update";

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

            options.AddPolicy(ExternalAuthNoncePolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: ResolveClientKey48(context),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 30,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));

            options.AddPolicy(ExternalAuthPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: ResolveClientKey48(context),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));

            options.AddPolicy(NameUpdatePolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: ResolveUserKey(context),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromHours(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));

            options.AddPolicy(PhoneUpdatePolicy, context => RateLimitPartition.GetFixedWindowLimiter(
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

    internal static string ResolveClientKey(IPAddress? address) => ResolveClientKey(address, ipv6PrefixBytes: 8);

    // Same as ResolveClientKey but IPv6 keys on the /48, for the two Google sign-in policies only
    // (ADR-030 section 7): one /48 holds 65 536 /64s, enough to fill the nonce store within minutes
    // if each /64 had its own budget. IPv4 and IPv4-mapped addresses are unchanged.
    internal static string ResolveClientKey48(HttpContext context) =>
        ResolveClientKey48(context.Connection.RemoteIpAddress);

    internal static string ResolveClientKey48(IPAddress? address) => ResolveClientKey(address, ipv6PrefixBytes: 6);

    private static string ResolveClientKey(IPAddress? address, int ipv6PrefixBytes)
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

        bytes[ipv6PrefixBytes..].Clear();
        return new IPAddress(bytes).ToString() + "/" + (ipv6PrefixBytes * 8);
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
