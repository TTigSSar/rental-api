using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Services;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Infrastructure.Services;

namespace RentalPlatform.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
        }

        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlServer(connectionString));

        services.AddHttpContextAccessor();

        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidation>();
        services.Configure<LocalFileStorageOptions>(configuration.GetSection(LocalFileStorageOptions.SectionName));
        services.AddSingleton<IValidateOptions<LocalFileStorageOptions>, LocalFileStorageOptionsValidation>();

        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserAuthStore, UserAuthStore>();
        services.AddScoped<IPasswordHasher, BcryptPasswordHasher>();
        services.AddScoped<ICurrentUserContext, CurrentUserContext>();
        services.AddScoped<IJwtTokenService, JwtTokenService>();
        services.AddScoped<IListingsQueryService, ListingsQueryService>();
        services.AddScoped<IListingsOwnerService, ListingsOwnerService>();
        services.AddScoped<IListingImagesOwnerService, ListingImagesOwnerService>();
        services.AddScoped<ICategoriesQueryService, CategoriesQueryService>();
        services.AddScoped<IListingsOwnerStore, ListingsOwnerStore>();
        services.AddSingleton<IFileStorageService, LocalFileStorageService>();

        return services;
    }
}

internal sealed class LocalFileStorageOptionsValidation : IValidateOptions<LocalFileStorageOptions>
{
    public ValidateOptionsResult Validate(string? name, LocalFileStorageOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ListingsImagesPath))
        {
            return ValidateOptionsResult.Fail("FileStorage:ListingsImagesPath is required.");
        }

        return ValidateOptionsResult.Success;
    }
}

internal sealed class JwtOptionsValidation : IValidateOptions<JwtOptions>
{
    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Issuer))
        {
            return ValidateOptionsResult.Fail("Jwt:Issuer is required.");
        }

        if (string.IsNullOrWhiteSpace(options.Audience))
        {
            return ValidateOptionsResult.Fail("Jwt:Audience is required.");
        }

        if (string.IsNullOrWhiteSpace(options.SecretKey))
        {
            return ValidateOptionsResult.Fail("Jwt:SecretKey is required.");
        }

        if (options.SecretKey.Length < 32)
        {
            return ValidateOptionsResult.Fail("Jwt:SecretKey must be at least 32 characters.");
        }

        if (options.AccessTokenExpirationMinutes <= 0)
        {
            return ValidateOptionsResult.Fail("Jwt:AccessTokenExpirationMinutes must be greater than zero.");
        }

        return ValidateOptionsResult.Success;
    }
}
