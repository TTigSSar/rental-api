using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using RentalPlatform.Api.Hubs;
using RentalPlatform.Api.Serialization;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Infrastructure.DependencyInjection;
using RentalPlatform.Infrastructure.Services;
using System.Security.Claims;

namespace RentalPlatform.Api.Extensions;

public static class ServiceCollectionExtensions
{
    public const string FrontendCorsPolicy = "FrontendCorsPolicy";

    public static IServiceCollection AddApiServices(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        var jwtSettings = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
            ?? throw new InvalidOperationException("JWT settings are not configured.");
        ValidateJwtOptions(jwtSettings, environment);

        services.AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
                options.JsonSerializerOptions.Converters.Add(new UtcDateTimeJsonConverter());
                options.JsonSerializerOptions.Converters.Add(new UtcNullableDateTimeJsonConverter());
            });
        services.AddEndpointsApiExplorer();
        services.AddCors(options =>
        {
            var configuredOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
            var allowedOrigins = configuredOrigins
                .Where(origin => !string.IsNullOrWhiteSpace(origin))
                .Select(origin => origin.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            options.AddPolicy(FrontendCorsPolicy, policyBuilder =>
            {
                policyBuilder
                    .SetIsOriginAllowed(origin => IsAllowedOrigin(origin, allowedOrigins, environment.IsDevelopment()))
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    // Required for the SignalR chat hub's WebSocket transport, which needs
                    // credentialed cross-origin requests. Safe alongside SetIsOriginAllowed
                    // (unlike AllowAnyOrigin, which AllowCredentials cannot be combined with).
                    .AllowCredentials();
            });
        });

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    ValidIssuer = jwtSettings.Issuer,
                    ValidAudience = jwtSettings.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.SecretKey)),
                    NameClaimType = ClaimTypes.Email,
                    RoleClaimType = ClaimTypes.Role,
                    ClockSkew = TimeSpan.Zero
                };

                // Browsers cannot set the Authorization header on the WebSocket handshake used
                // by the SignalR chat hub, so accept the access token via query string for that
                // path only. Standard SignalR pattern; see the client's withUrl(..., { accessTokenFactory }).
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        var path = context.HttpContext.Request.Path;

                        if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs/chat"))
                        {
                            context.Token = accessToken;
                        }

                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorization();

        services.AddSignalR();
        services.AddScoped<IChatRealtimeNotifier, ChatRealtimeNotifier>();

        services.AddSwaggerGen(options =>
        {
            options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Name = "Authorization",
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
                Description = "Provide the JWT access token."
            });

            options.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference
                        {
                            Type = ReferenceType.SecurityScheme,
                            Id = "Bearer"
                        }
                    },
                    Array.Empty<string>()
                }
            });
        });

        services.AddApiRateLimiting();
        services.AddProxyForwardedHeaders(configuration);
        services.AddInfrastructure(configuration);

        return services;
    }

    private static void ValidateJwtOptions(JwtOptions jwtOptions, IWebHostEnvironment environment)
    {
        if (string.IsNullOrWhiteSpace(jwtOptions.Issuer))
        {
            throw new InvalidOperationException("Jwt:Issuer is required.");
        }

        if (string.IsNullOrWhiteSpace(jwtOptions.Audience))
        {
            throw new InvalidOperationException("Jwt:Audience is required.");
        }

        if (string.IsNullOrWhiteSpace(jwtOptions.SecretKey))
        {
            throw new InvalidOperationException(
                "Jwt:SecretKey is required. Provide a strong value via environment variable Jwt__SecretKey or user secrets (never commit it).");
        }

        if (jwtOptions.SecretKey.Length < 32)
        {
            throw new InvalidOperationException("Jwt:SecretKey must be at least 32 characters.");
        }

        if (jwtOptions.AccessTokenExpirationMinutes <= 0)
        {
            throw new InvalidOperationException("Jwt:AccessTokenExpirationMinutes must be greater than zero.");
        }

        // Non-Development environments must never boot with a bundled placeholder/dev secret.
        // The Development appsettings ships a long-but-known string for demos; in any other
        // environment we require an operator-supplied value.
        if (!environment.IsDevelopment() && LooksLikePlaceholderSecret(jwtOptions.SecretKey))
        {
            throw new InvalidOperationException(
                "Jwt:SecretKey looks like a development placeholder. Set a strong, unique value via environment variable Jwt__SecretKey or user secrets before running outside Development.");
        }
    }

    private static bool LooksLikePlaceholderSecret(string secret) =>
        secret.Contains("development-only", StringComparison.OrdinalIgnoreCase)
        || secret.Contains("change-before", StringComparison.OrdinalIgnoreCase)
        || secret.Contains("placeholder", StringComparison.OrdinalIgnoreCase)
        || secret.Contains("changeme", StringComparison.OrdinalIgnoreCase)
        || secret.Contains("example", StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowedOrigin(string origin, IReadOnlySet<string> allowedOrigins, bool isDevelopment)
    {
        if (allowedOrigins.Contains(origin))
        {
            return true;
        }

        if (!isDevelopment || !Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var isLoopbackHost = uri.IsLoopback ||
                             string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase);

        var isHttpScheme = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

        return isLoopbackHost && isHttpScheme;
    }
}
