using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Infrastructure.DependencyInjection;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// Exercises DemoContentBootstrapExtensions.ParseDemoContentEnabled — the tolerant parser for
// Bootstrap:DemoContentEnabled that replaced a bare configuration.GetValue<bool>(...). That call
// threw InvalidOperationException both on "" (Docker Compose maps an unset/blank .env var to an
// empty string, not an absent key — the key is still present) and on any typo ("yes", "1", ...).
// Since this read happens on the startup path, any of those inputs used to crash-loop the whole
// container — and because ui/cloudflared gate on api's `service_healthy` in
// docker-compose.production.yml, that is a full site outage over what is meant to be a purely
// optional, cosmetic feature (an initial demo catalogue). A bad value here must degrade to
// "feature off", never to "app dead".
public sealed class DemoContentEnabledParsingTests
{
    private static IConfiguration BuildConfiguration(string? value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DemoContentBootstrapExtensions.DemoContentEnabledKey] = value
            })
            .Build();

    [Fact]
    public void Absent_Key_Disables_Silently()
    {
        var configuration = new ConfigurationBuilder().Build(); // key never set at all
        var logger = new CapturingLogger();

        var enabled = DemoContentBootstrapExtensions.ParseDemoContentEnabled(configuration, logger);

        Assert.False(enabled);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Empty_String_Disables_Without_Throwing_Or_Logging()
    {
        // This is exactly the value Docker Compose produces for an unset/blank .env var (the key
        // is still set, just to "") — the scenario that used to crash the container before this fix.
        var configuration = BuildConfiguration("");
        var logger = new CapturingLogger();

        var enabled = DemoContentBootstrapExtensions.ParseDemoContentEnabled(configuration, logger);

        Assert.False(enabled);
        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("on")]
    [InlineData("Troo")]
    public void Unparseable_Value_Disables_And_Logs_A_Warning(string garbage)
    {
        var configuration = BuildConfiguration(garbage);
        var logger = new CapturingLogger();

        var enabled = DemoContentBootstrapExtensions.ParseDemoContentEnabled(configuration, logger);

        Assert.False(enabled);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(DemoContentBootstrapExtensions.DemoContentEnabledKey, entry.Message);
        Assert.Contains(garbage, entry.Message);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    public void Valid_Bool_Spelling_Parses_Exactly_And_Does_Not_Log(string value, bool expected)
    {
        var configuration = BuildConfiguration(value);
        var logger = new CapturingLogger();

        var enabled = DemoContentBootstrapExtensions.ParseDemoContentEnabled(configuration, logger);

        Assert.Equal(expected, enabled);
        Assert.Empty(logger.Entries);
    }

    // End-to-end through the real public entry point (BootstrapDemoContentAsync), with a real
    // AppDbContext/DI graph — proves the wiring, not just the parser, survives the exact input
    // that would have reached Program.cs on a fresh production deploy with the documented
    // .env.production.example defaults (BOOTSTRAP_DEMO_CONTENT_ENABLED unset).
    [Fact]
    public async Task BootstrapDemoContentAsync_Does_Not_Throw_On_Empty_Flag_Value()
    {
        using var db = new SqliteTestDatabase();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(db.CreateContext());
        services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
        services.AddSingleton<IFileStorageService, FakeFileStorageService>();
        var provider = services.BuildServiceProvider();

        var configuration = BuildConfiguration(""); // no owner creds either -> stays a full no-op

        var exception = await Record.ExceptionAsync(() => provider.BootstrapDemoContentAsync(configuration));

        Assert.Null(exception);
    }

    [Fact]
    public async Task BootstrapDemoContentAsync_Does_Not_Throw_On_Garbage_Flag_Value()
    {
        using var db = new SqliteTestDatabase();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(db.CreateContext());
        services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
        services.AddSingleton<IFileStorageService, FakeFileStorageService>();
        var provider = services.BuildServiceProvider();

        var configuration = BuildConfiguration("yes"); // no owner creds either -> stays a full no-op

        var exception = await Record.ExceptionAsync(() => provider.BootstrapDemoContentAsync(configuration));

        Assert.Null(exception);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
