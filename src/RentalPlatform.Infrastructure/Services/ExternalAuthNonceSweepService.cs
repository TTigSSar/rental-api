using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RentalPlatform.Infrastructure.Services;

// Drops expired nonces every 60 s so abandoned ones do not hold capacity (ADR-030 section 2).
// Expiry is also enforced on consume, so correctness never depends on this sweep having run.
public sealed class ExternalAuthNonceSweepService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(60);

    private readonly ExternalAuthNonceStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ExternalAuthNonceSweepService> _logger;

    public ExternalAuthNonceSweepService(
        ExternalAuthNonceStore store,
        TimeProvider timeProvider,
        ILogger<ExternalAuthNonceSweepService> logger)
    {
        _store = store;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    _store.SweepExpired(_timeProvider.GetUtcNow());
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Failed to sweep expired external auth nonces.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }
}
