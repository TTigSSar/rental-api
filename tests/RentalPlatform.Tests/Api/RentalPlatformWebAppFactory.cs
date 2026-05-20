using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Api;

// Shared integration-test host. Replaces the SQL Server DbContext with an in-memory
// SQLite connection and swaps LocalFileStorageService for an in-memory double.
//
// "IntegrationTest" environment is injected via ASPNETCORE_ENVIRONMENT before the host
// starts, so:
//   - The development seed does NOT run (no bcrypt overhead, no extra rows).
//   - The JWT placeholder check runs but our test key passes it (≥32 chars, no keywords).
//
// JWT config is supplied via environment variables because WebApplicationBuilder reads them
// during WebApplication.CreateBuilder(), which runs BEFORE ConfigureWebHost callbacks fire.
// ConfigureAppConfiguration would be too late — AddApiServices.ValidateJwtOptions reads the
// key during service registration, not during IHost.StartAsync().
public sealed class RentalPlatformWebAppFactory : WebApplicationFactory<Program>
{
    // Shared with TestJwtTokenHelper — both must agree on these values.
    internal const string JwtSecretKey = "xunit-integration-test-signing-key-abc123";
    internal const string JwtIssuer = "RentalPlatform";
    internal const string JwtAudience = "RentalPlatform.Api";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public FakeFileStorageService FakeStorage { get; } = new();

    public RentalPlatformWebAppFactory()
    {
        _connection.Open();

        // Must be set before EnsureServer() triggers Program.cs so that
        // WebApplication.CreateBuilder() picks up the correct environment and JWT key.
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "IntegrationTest");
        Environment.SetEnvironmentVariable("Jwt__SecretKey", JwtSecretKey);
        Environment.SetEnvironmentVariable("Jwt__Issuer", JwtIssuer);
        Environment.SetEnvironmentVariable("Jwt__Audience", JwtAudience);
        Environment.SetEnvironmentVariable("Jwt__AccessTokenExpirationMinutes", "60");

        // Satisfies AddInfrastructure's non-empty check; actual DbContext replaced below.
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", "DataSource=:memory:");

        // FileStorage options ValidateOnStart check.
        Environment.SetEnvironmentVariable("FileStorage__ListingsImagesPath", "uploads/listings");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            // Replace the SQL Server DbContext with the shared in-memory SQLite connection.
            // ConfigureTestServices runs inside IHostBuilder.Build() — after AddApiServices
            // has registered the SQL Server DbContext — so this replacement is the one used.
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.AddDbContext<AppDbContext>(opts => opts.UseSqlite(_connection));

            // Replace disk-backed file storage with an in-memory double.
            services.RemoveAll<IFileStorageService>();
            services.AddSingleton<IFileStorageService>(FakeStorage);
        });
    }

    // Seeds entities through a dedicated scope so no acting scope inherits a dirty
    // change tracker. Accessing Services on first call triggers the lazy host build
    // (Program.cs runs, ApplyMigrationsAsync → EnsureCreated for SQLite, schema ready).
    public async Task SeedAsync(params object[] entities)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AddRange(entities);
        await db.SaveChangesAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _connection.Dispose();
        base.Dispose(disposing);
    }
}

// One collection fixture keeps the host (and the SQLite schema) alive across all
// integration-test classes without rebuilding it per class. Tests within the
// collection run sequentially so rate-limit counters remain stable and predictable.
[CollectionDefinition("Integration")]
public sealed class IntegrationCollection : ICollectionFixture<RentalPlatformWebAppFactory> { }
