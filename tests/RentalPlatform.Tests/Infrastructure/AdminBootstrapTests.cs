using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.DependencyInjection;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// Exercises AdminBootstrapExtensions.BootstrapAdminAsync — the production-only mechanism
// that creates the first Admin user on a fresh database (Development gets an admin via the
// dev seed instead; every other environment starts empty). Goes through the real
// IPasswordHasher (BCrypt) and the public extension method, with an in-memory IUserAuthStore
// standing in for the database.
public sealed class AdminBootstrapTests
{
    private static IServiceProvider BuildServices(FakeUserAuthStore store)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IUserAuthStore>(store);
        services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
        return services.BuildServiceProvider();
    }

    private static IConfiguration BuildConfiguration(string? email, string? password) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bootstrap:AdminEmail"] = email,
                ["Bootstrap:AdminPassword"] = password
            })
            .Build();

    [Fact]
    public async Task Creates_Admin_User_With_Hashed_Password_When_Configured_And_Absent()
    {
        var store = new FakeUserAuthStore();
        var provider = BuildServices(store);
        var configuration = BuildConfiguration("Admin@Example.com", "SuperSecret123");

        await provider.BootstrapAdminAsync(configuration);

        var created = Assert.Single(store.Users);
        Assert.Equal("admin@example.com", created.Email); // normalized: trimmed + lowercased
        Assert.Equal(UserRole.Admin, created.Role);
        Assert.NotEqual("SuperSecret123", created.PasswordHash); // never stored raw
        Assert.True(BCrypt.Net.BCrypt.Verify("SuperSecret123", created.PasswordHash));
    }

    [Fact]
    public async Task Is_NoOp_When_User_With_That_Email_Already_Exists()
    {
        var store = new FakeUserAuthStore()
            .Seed(TestData.User(Guid.NewGuid(), "admin@example.com", role: UserRole.Admin));
        var provider = BuildServices(store);
        var configuration = BuildConfiguration("admin@example.com", "SomeOtherPassword1");

        await provider.BootstrapAdminAsync(configuration);

        // Still exactly one user, and its password hash is untouched (still the seeded "x").
        var only = Assert.Single(store.Users);
        Assert.Equal("x", only.PasswordHash);
    }

    [Theory]
    [InlineData(null, "SomePassword1")]
    [InlineData("admin@example.com", null)]
    [InlineData("", "SomePassword1")]
    [InlineData("admin@example.com", "")]
    [InlineData(null, null)]
    public async Task Is_NoOp_When_Either_Config_Value_Is_Missing(string? email, string? password)
    {
        var store = new FakeUserAuthStore();
        var provider = BuildServices(store);
        var configuration = BuildConfiguration(email, password);

        await provider.BootstrapAdminAsync(configuration);

        Assert.Empty(store.Users);
    }
}
