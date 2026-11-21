using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Tests.TestSupport;

// Scriptable IAvatarImageProcessor: no image library, returns whatever the test says.
public sealed class FakeAvatarImageProcessor : IAvatarImageProcessor
{
    public AvatarProcessingStatus Status { get; set; } = AvatarProcessingStatus.Ok;
    public Exception? Throw { get; set; }
    public int Calls { get; private set; }

    public Task<AvatarProcessingResult> ProcessAsync(Stream content, long length, CancellationToken cancellationToken = default)
    {
        Calls++;
        if (Throw is not null)
        {
            throw Throw;
        }

        return Task.FromResult(new AvatarProcessingResult(Status, Status == AvatarProcessingStatus.Ok ? new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 } : null));
    }
}

// Decorates the real store so a test can act "between" the service's read and its conditional update, or force
// an outcome, without any timing luck.
public sealed class HookAvatarStore : IAvatarStore
{
    private readonly IAvatarStore _inner;

    public HookAvatarStore(IAvatarStore inner) => _inner = inner;

    // Runs before the real TryReplace of attempt N (1-based). Used to change the row so the attempt loses.
    public Func<int, Task>? BeforeTryReplace { get; set; }

    // When set, replaces the real outcome (the real update is NOT run).
    public Func<int, AvatarCasOutcome?>? ForcedOutcome { get; set; }

    public List<(string? ExpectedOld, string? NewUrl)> Attempts { get; } = new();

    public Task<AvatarUserState?> GetStateAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _inner.GetStateAsync(userId, cancellationToken);

    public async Task<AvatarCasOutcome> TryReplaceAsync(Guid userId, string? expectedOld, string? newUrl, CancellationToken cancellationToken = default)
    {
        Attempts.Add((expectedOld, newUrl));
        var attempt = Attempts.Count;
        if (BeforeTryReplace is not null)
        {
            await BeforeTryReplace(attempt);
        }

        if (ForcedOutcome?.Invoke(attempt) is { } forced)
        {
            return forced;
        }

        return await _inner.TryReplaceAsync(userId, expectedOld, newUrl, cancellationToken);
    }

    public void MirrorCommitted(User user, string? newUrl) => _inner.MirrorCommitted(user, newUrl);
}
