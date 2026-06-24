using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentalPlatform.Application.Abstractions;

namespace RentalPlatform.Infrastructure.Services;

// Periodically expires stale pending bookings. Previously this sweep ran inline on every booking
// read/write, adding two UPDATE statements (and lock contention) to plain reads. Running it on a
// timer keeps request paths read-only; the create/approve paths stay correct independently because
// they evaluate ExpiresAt directly rather than trusting the sweep to have run.
public sealed class BookingExpiryBackgroundService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BookingExpiryBackgroundService> _logger;

    public BookingExpiryBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<BookingExpiryBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);

        // Run once at startup, then on each tick.
        do
        {
            await SweepAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var bookingsStore = scope.ServiceProvider.GetRequiredService<IBookingsStore>();
            await bookingsStore.ExpirePendingAsync(DateTime.UtcNow, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down — nothing to do.
        }
        catch (Exception ex)
        {
            // A transient failure must not tear down the host; the next tick retries.
            _logger.LogError(ex, "Failed to expire stale pending bookings.");
        }
    }
}
