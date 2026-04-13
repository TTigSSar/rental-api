using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RentalPlatform.Infrastructure.DependencyInjection;

namespace RentalPlatform.Api.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApiServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddControllers();
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();

        services.AddInfrastructure(configuration);

        return services;
    }
}
