using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// MUST-FIX 2 from the 2026-08 code review: ModerationLogStore.AppendAsync always runs after the
// caller's business mutation has already committed (AdminReportsService/AdminListingsService/
// AdminUsersService/AdminCategoriesService), so — same doctrine as IEmailService — a failure here
// must never invert the client's view of a committed mutation. AppendAsync must swallow and log
// its own failures instead of propagating them.
public sealed class ModerationLogStoreTests
{
    private static readonly Guid ActorId = new("f0000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task AppendAsync_Swallows_A_Save_Failure_Instead_Of_Throwing()
    {
        using var db = new SqliteTestDatabase();
        await db.SeedAsync(TestData.User(ActorId, "actor@test.local", role: UserRole.Admin));

        var duplicateId = Guid.NewGuid();
        var existing = new ModerationLogEntry
        {
            Id = duplicateId,
            ActorUserId = ActorId,
            Action = ModerationAction.UserVerified,
            TargetType = ModerationTargetType.User,
            TargetId = Guid.NewGuid(),
            TargetLabel = "Existing entry",
            DetailJson = null,
            CreatedAt = DateTime.UtcNow
        };
        await db.SeedAsync(existing);

        var logger = new CapturingLogger<ModerationLogStore>();
        await using var context = db.CreateContext();
        var store = new ModerationLogStore(context, logger);

        // Same primary key as the row already seeded above -> SQLite's own PK uniqueness
        // constraint (always enforced, unlike HasMaxLength) makes SaveChangesAsync throw here,
        // standing in for the DetailJson-overflow/FK-violation failures MUST-FIX 1 was about.
        var colliding = new ModerationLogEntry
        {
            Id = duplicateId,
            ActorUserId = ActorId,
            Action = ModerationAction.UserSuspended,
            TargetType = ModerationTargetType.User,
            TargetId = Guid.NewGuid(),
            TargetLabel = "Colliding entry",
            DetailJson = null,
            CreatedAt = DateTime.UtcNow
        };

        var exception = await Record.ExceptionAsync(() => store.AppendAsync(colliding));

        Assert.Null(exception);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);

        // The failed entry must not linger in THIS SAME scoped context's change tracker and
        // sabotage a later, unrelated save in the same request — reusing `context` (not a fresh
        // one) is the point of this assertion.
        context.ModerationLogEntries.Add(new ModerationLogEntry
        {
            Id = Guid.NewGuid(),
            ActorUserId = ActorId,
            Action = ModerationAction.UserReactivated,
            TargetType = ModerationTargetType.User,
            TargetId = Guid.NewGuid(),
            TargetLabel = "Unrelated later entry",
            DetailJson = null,
            CreatedAt = DateTime.UtcNow
        });
        var laterSaveException = await Record.ExceptionAsync(() => context.SaveChangesAsync());
        Assert.Null(laterSaveException);

        await using var verify = db.CreateContext();
        // The original row survives untouched; the colliding append never landed.
        var stored = await verify.ModerationLogEntries.SingleAsync(e => e.Id == duplicateId);
        Assert.Equal("Existing entry", stored.TargetLabel);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
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
