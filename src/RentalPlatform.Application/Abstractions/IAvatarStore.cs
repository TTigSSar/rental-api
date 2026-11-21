using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Abstractions;

public sealed record AvatarUserState(string? AvatarUrl, bool IsDeleted, bool IsBlocked);

public enum AvatarCasOutcome
{
    Committed = 0,

    /// <summary>The stored value no longer equals the expected one (someone else changed it).</summary>
    Mismatch = 1,
    UserDeleted = 2,
    UserBlocked = 3,

    /// <summary>The transaction commit itself threw: the new value may or may not be stored.</summary>
    CommitAmbiguous = 4
}

// ADR-070 point 9: a conditional update on Users.AvatarUrl.
public interface IAvatarStore
{
    /// <summary>Fresh, untracked read. Null when the user row does not exist.</summary>
    Task<AvatarUserState?> GetStateAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// One transaction: <c>UPDATE Users SET AvatarUrl = @newUrl WHERE Id AND DeletedAt IS NULL AND IsBlocked = 0
    /// AND (AvatarUrl IS NULL | AvatarUrl = @expectedOld)</c>. On zero rows the state is re-read to say why.
    /// </summary>
    Task<AvatarCasOutcome> TryReplaceAsync(Guid userId, string? expectedOld, string? newUrl, CancellationToken cancellationToken = default);

    /// <summary>After a confirmed commit: copy the value into the tracked entity without leaving it Modified.</summary>
    void MirrorCommitted(User user, string? newUrl);
}
