using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .Validate(static options => !string.IsNullOrWhiteSpace(options.Issuer), "Jwt:Issuer is required.")
            .Validate(static options => !string.IsNullOrWhiteSpace(options.Audience), "Jwt:Audience is required.")
            .Validate(static options => !string.IsNullOrWhiteSpace(options.SecretKey), "Jwt:SecretKey is required.")
            .Validate(static options => options.SecretKey.Length >= 32, "Jwt:SecretKey must be at least 32 characters.")
            .Validate(static options => options.AccessTokenExpirationMinutes > 0, "Jwt:AccessTokenExpirationMinutes must be greater than zero.")
            .ValidateOnStart();

        services.AddOptions<LocalFileStorageOptions>()
            .Bind(configuration.GetSection(LocalFileStorageOptions.SectionName))
            .Validate(static options => !string.IsNullOrWhiteSpace(options.ListingsImagesPath), "FileStorage:ListingsImagesPath is required.")
            .ValidateOnStart();

        services.AddOptions<ExternalAuthOptions>()
            .Bind(configuration.GetSection(ExternalAuthOptions.SectionName));

        services.AddHttpClient(nameof(ExternalIdentityTokenValidator));
        services.AddSingleton(TimeProvider.System);

        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserAuthStore, UserAuthStore>();
        services.AddScoped<IExternalIdentityTokenValidator, ExternalIdentityTokenValidator>();
        services.AddScoped<IPasswordHasher, BcryptPasswordHasher>();
        services.AddScoped<ICurrentUserContext, CurrentUserContext>();
        services.AddScoped<IJwtTokenService, JwtTokenService>();
        services.AddScoped<IListingsQueryService, ListingsQueryService>();
        services.AddScoped<IPublicUserProfileService, PublicUserProfileQueryService>();
        services.AddScoped<IHomeSectionsService, HomeSectionsQueryService>();
        services.AddScoped<IEmailService, DevelopmentEmailService>();
        services.AddScoped<IListingsOwnerService, ListingsOwnerService>();
        services.AddScoped<IListingImagesOwnerService, ListingImagesOwnerService>();
        services.AddScoped<ICategoriesQueryService, CategoriesQueryService>();
        services.AddScoped<IBookingsService, BookingsService>();
        services.AddScoped<IReviewsService, ReviewsService>();
        services.AddScoped<IFavoritesService, FavoritesService>();
        services.AddScoped<IAdminListingsService, AdminListingsService>();
        services.AddScoped<INotificationsService, NotificationsService>();
        services.AddScoped<INotificationEmitter, NotificationEmitter>();
        services.AddScoped<IChatService, ChatService>();
        services.AddScoped<IChatSystemMessageEmitter, ChatSystemMessageEmitter>();
        services.AddScoped<IListingsOwnerStore, ListingsOwnerStore>();
        services.AddScoped<IBookingsStore, BookingsStore>();
        services.AddScoped<IFavoritesStore, FavoritesStore>();
        services.AddScoped<IAdminListingsStore, AdminListingsStore>();
        services.AddScoped<IReviewsStore, ReviewsStore>();
        services.AddScoped<INotificationsStore, NotificationsStore>();
        services.AddScoped<IConversationsStore, ConversationsStore>();
        services.AddSingleton<IFileStorageService, LocalFileStorageService>();

        services.AddHostedService<BookingExpiryBackgroundService>();

        return services;
    }
}
