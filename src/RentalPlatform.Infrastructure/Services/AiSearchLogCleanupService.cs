using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentalPlatform.Infrastructure.Persistence;

namespace RentalPlatform.Infrastructure.Services;

// Keeps the raw AI-search description for 30 days, then nulls it (ADR-054); the metadata row stays.
// Self-contained on purpose: ADR-048 names Hangfire for scheduled work, which is not installed, so
// this is one class plus one AddHostedService line and is trivially replaced by a Hangfire job that
// calls SweepAsync. Expiry never depends on this having run for correctness of anything else.
public sealed class AiSearchLogCleanupService : BackgroundService
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AiSearchLogCleanupService> _logger;

    public AiSearchLogCleanupService(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<AiSearchLogCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Nulls Description on rows older than the retention window. Returns the row count.</summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = _timeProvider.GetUtcNow().UtcDateTime - Retention;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.AiSearchLogs
            .Where(log => log.Description != null && log.CreatedAt < cutoff)
            .ExecuteUpdateAsync(setters => setters.SetProperty(log => log.Description, (string?)null), cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
            do
            {
                try
                {
                    var cleared = await SweepAsync(stoppingToken);
                    if (cleared > 0)
                    {
                        _logger.LogInformation("Cleared {Count} expired AI search descriptions.", cleared);
                    }
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(exception, "Failed to clear expired AI search descriptions.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }
}
