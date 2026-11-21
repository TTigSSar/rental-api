using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Infrastructure.Persistence;

namespace RentalPlatform.Infrastructure.Services;

// ADR-070 point 12 / plan 3.3: the eventual guarantee that no avatar file of an erased account survives, and
// the retry procedure for every best-effort delete. Same shape as AiSearchLogCleanupService (Hangfire is not
// installed): one hosted service, once 10 minutes after start and then every 24 hours. Never throws out of
// the loop; one summary line (EventId 6904) per run.
public sealed class AvatarOrphanSweepService : BackgroundService
{
    public static readonly EventId AvatarSweepSummaryEvent = new(6904, "AvatarSweepSummary");

    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan SweepInterval = TimeSpan.FromHours(24);

    // A file younger than this may belong to an upload that has not committed yet.
    public static readonly TimeSpan OrphanMinAge = TimeSpan.FromHours(1);

    private const int BatchSize = 200;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AvatarOrphanSweepService> _logger;

    public AvatarOrphanSweepService(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<AvatarOrphanSweepService> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public sealed record SweepSummary(int DirectoriesScanned, int DirectoriesWiped, int OrphansDeleted, int Failures, int Skipped);

    public async Task<SweepSummary> SweepAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();

        int scanned = 0, wiped = 0, orphans = 0, failures = 0, skipped = 0;
        var cutoff = _timeProvider.GetUtcNow().UtcDateTime - OrphanMinAge;

        IReadOnlyList<string> names;
        try
        {
            names = await storage.ListAvatarDirectoryNamesAsync(cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(exception, "Avatar sweep could not list the avatar directories.");
            var failed = new SweepSummary(0, 0, 0, 1, 0);
            LogSummary(failed);
            return failed;
        }

        var parsed = new List<Guid>();
        foreach (var name in names)
        {
            scanned++;
            if (Guid.TryParseExact(name, "N", out var id) && string.Equals(name, id.ToString("N", CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                parsed.Add(id);
            }
            else
            {
                skipped++;
                _logger.LogWarning("Avatar sweep skipped the directory {Directory}: its name is not a user id.", name);
            }
        }

        foreach (var batch in parsed.Chunk(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            Dictionary<Guid, (bool Deleted, string? AvatarUrl)> users;
            try
            {
                users = await db.Users
                    .AsNoTracking()
                    .Where(user => batch.Contains(user.Id))
                    .Select(user => new { user.Id, Deleted = user.DeletedAt != null, user.AvatarUrl })
                    .ToDictionaryAsync(row => row.Id, row => (row.Deleted, row.AvatarUrl), cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                failures += batch.Length;
                _logger.LogError(exception, "Avatar sweep could not look up a batch of users.");
                continue;
            }

            foreach (var userId in batch)
            {
                try
                {
                    if (!users.TryGetValue(userId, out var user) || user.Deleted)
                    {
                        if (await storage.DeleteAvatarDirectoryAsync(userId, cancellationToken))
                        {
                            wiped++;
                        }
                        else
                        {
                            failures++;
                        }

                        continue;
                    }

                    foreach (var file in await storage.ListAvatarFilesAsync(userId, cancellationToken))
                    {
                        if (file.LastWriteUtc >= cutoff || string.Equals(file.Url, user.AvatarUrl, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (await storage.DeleteAvatarAsync(userId, file.Url, cancellationToken))
                        {
                            orphans++;
                        }
                        else
                        {
                            failures++;
                        }
                    }
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    failures++;
                    _logger.LogError(exception, "Avatar sweep failed for the directory of user {UserId}.", userId);
                }
            }
        }

        var summary = new SweepSummary(scanned, wiped, orphans, failures, skipped);
        LogSummary(summary);
        return summary;
    }

    private void LogSummary(SweepSummary summary) =>
        _logger.LogInformation(
            AvatarSweepSummaryEvent,
            "Avatar sweep: {Scanned} directories scanned, {Wiped} wiped, {Orphans} orphan files deleted, {Failures} failures, {Skipped} skipped.",
            summary.DirectoriesScanned,
            summary.DirectoriesWiped,
            summary.OrphansDeleted,
            summary.Failures,
            summary.Skipped);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, _timeProvider, stoppingToken);
            using var timer = new PeriodicTimer(SweepInterval, _timeProvider);
            do
            {
                try
                {
                    await SweepAsync(stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(exception, "Avatar orphan sweep failed.");
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
