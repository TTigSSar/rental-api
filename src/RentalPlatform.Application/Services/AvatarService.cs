using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Services;

// ADR-070: upload / replace / remove the caller's own profile picture.
//
// Order of an upload: auth + blocked + deleted checks (no file is written for a deleted account) ->
// process (re-encode) -> save once -> conditional update on Users.AvatarUrl, up to three attempts, the
// expected old value re-read per attempt -> after a confirmed commit, delete ONLY the old value that
// attempt committed over. Anything uncommitted deletes the new file again (except an ambiguous commit,
// which keeps it: the sweep reconciles). Notification snapshots are never rewritten (ADR-070 point 8).
public sealed class AvatarService : IAvatarService
{
    public static class ErrorCodes
    {
        public const string Unauthenticated = "auth.unauthenticated";
        public const string UserBlocked = "auth.user_blocked";
        public const string Missing = "auth.avatar_missing";
        public const string Empty = "auth.avatar_empty";
        public const string InvalidType = "auth.avatar_invalid_type";
        public const string TooLarge = "auth.avatar_too_large";
        public const string Conflict = "auth.avatar_conflict";
        public const string Busy = "auth.avatar_busy";
    }

    public const int MaxCasAttempts = 3;

    private readonly ICurrentUserContext _currentUserContext;
    private readonly IUserAuthStore _userAuthStore;
    private readonly IAvatarStore _avatarStore;
    private readonly IAvatarImageProcessor _processor;
    private readonly IFileStorageService _fileStorage;

    public AvatarService(
        ICurrentUserContext currentUserContext,
        IUserAuthStore userAuthStore,
        IAvatarStore avatarStore,
        IAvatarImageProcessor processor,
        IFileStorageService fileStorage)
    {
        _currentUserContext = currentUserContext;
        _userAuthStore = userAuthStore;
        _avatarStore = avatarStore;
        _processor = processor;
        _fileStorage = fileStorage;
    }

    public async Task<ServiceResult<CurrentUserResponse>> UploadAsync(AvatarContent? content, CancellationToken cancellationToken = default)
    {
        var (user, error) = await LoadActiveUserAsync(cancellationToken);
        if (user is null)
        {
            return Fail(error!);
        }

        if (content is null)
        {
            return Fail(ErrorCodes.Missing, "Exactly one image file is required.");
        }

        if (content.Length <= 0)
        {
            return Fail(ErrorCodes.Empty, "The uploaded file is empty.");
        }

        var processed = await _processor.ProcessAsync(content.Stream, content.Length, cancellationToken);
        switch (processed.Status)
        {
            case AvatarProcessingStatus.InvalidType:
                return Fail(ErrorCodes.InvalidType, "The file is not a supported image (JPEG, PNG or WebP).");
            case AvatarProcessingStatus.TooLarge:
                return Fail(ErrorCodes.TooLarge, "The image is too large.");
            case AvatarProcessingStatus.Busy:
                return Fail(ErrorCodes.Busy, "Image processing is busy, try again in a few seconds.", retryAfterSeconds: 5);
        }

        string newUrl;
        using (var jpeg = new MemoryStream(processed.Jpeg!, writable: false))
        {
            newUrl = await _fileStorage.SaveAvatarAsync(jpeg, user.Id, cancellationToken);
        }

        var keepNewFile = false;
        var committed = false;
        try
        {
            var outcome = await ReplaceAsync(user, newUrl, cancellationToken);
            committed = outcome.Committed;
            keepNewFile = outcome.KeepNewFile;
            if (outcome.Error is not null)
            {
                return Fail(outcome.Error);
            }

            await DeleteOldAsync(user.Id, outcome.CommittedOld);
            return ServiceResult<CurrentUserResponse>.Success(AuthService.MapUser(user));
        }
        finally
        {
            if (!committed && !keepNewFile)
            {
                // CancellationToken.None inside: the caller may be gone, the file must still go.
                await TryDeleteAsync(user.Id, newUrl);
            }
        }
    }

    public async Task<ServiceResult<CurrentUserResponse>> RemoveAsync(CancellationToken cancellationToken = default)
    {
        var (user, error) = await LoadActiveUserAsync(cancellationToken);
        if (user is null)
        {
            return Fail(error!);
        }

        var outcome = await ReplaceAsync(user, null, cancellationToken);
        if (outcome.Error is not null)
        {
            return Fail(outcome.Error);
        }

        await DeleteOldAsync(user.Id, outcome.CommittedOld);
        return ServiceResult<CurrentUserResponse>.Success(AuthService.MapUser(user));
    }

    private readonly record struct ReplaceOutcome(bool Committed, string? CommittedOld, ServiceError? Error, bool KeepNewFile);

    // The CAS loop (plan 3.2). Each attempt pins its own expectedOld; only the winning attempt's value is
    // reported back as CommittedOld.
    private async Task<ReplaceOutcome> ReplaceAsync(User user, string? newUrl, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxCasAttempts; attempt++)
        {
            var state = await _avatarStore.GetStateAsync(user.Id, cancellationToken);
            if (state is null || state.IsDeleted)
            {
                return await DeletedAsync(user.Id);
            }

            if (state.IsBlocked)
            {
                return new ReplaceOutcome(false, null, Blocked(), false);
            }

            if (newUrl is null && state.AvatarUrl is null)
            {
                // Remove is idempotent.
                _avatarStore.MirrorCommitted(user, null);
                return new ReplaceOutcome(true, null, null, false);
            }

            var expectedOld = state.AvatarUrl;
            var outcome = await _avatarStore.TryReplaceAsync(user.Id, expectedOld, newUrl, cancellationToken);
            switch (outcome)
            {
                case AvatarCasOutcome.Committed:
                    _avatarStore.MirrorCommitted(user, newUrl);
                    return new ReplaceOutcome(true, expectedOld, null, false);
                case AvatarCasOutcome.Mismatch:
                    continue;
                case AvatarCasOutcome.UserDeleted:
                    return await DeletedAsync(user.Id);
                case AvatarCasOutcome.UserBlocked:
                    return new ReplaceOutcome(false, null, Blocked(), false);
                case AvatarCasOutcome.CommitAmbiguous:
                    // Keep newUrl, delete nothing: the store logged it, the sweep reconciles.
                    return new ReplaceOutcome(false, null, Err(ErrorCodes.Conflict, "The picture may not have been saved; reload and check."), true);
            }
        }

        return new ReplaceOutcome(false, null, Err(ErrorCodes.Conflict, "Your picture was changed by another request, try again."), false);
    }

    // The account was erased while we worked: wipe the directory (our new file lives there) and answer 401.
    private async Task<ReplaceOutcome> DeletedAsync(Guid userId)
    {
        try
        {
            await _fileStorage.DeleteAvatarDirectoryAsync(userId, CancellationToken.None);
        }
        catch
        {
            // Best effort; the orphan sweep removes it within 24 h.
        }

        return new ReplaceOutcome(false, null, Err(ErrorCodes.Unauthenticated, "Current user is not authenticated."), false);
    }

    private async Task DeleteOldAsync(Guid userId, string? committedOld)
    {
        if (!string.IsNullOrWhiteSpace(committedOld))
        {
            await TryDeleteAsync(userId, committedOld);
        }
    }

    private async Task TryDeleteAsync(Guid userId, string url)
    {
        try
        {
            await _fileStorage.DeleteAvatarAsync(userId, url, CancellationToken.None);
        }
        catch
        {
            // The storage logs its own failures (AvatarOldFileDeleteFailed, 6901); the sweep is the retry.
        }
    }

    // user id -> load -> deleted/blocked checks, the preamble every self-service write shares.
    private async Task<(User? User, ServiceError? Error)> LoadActiveUserAsync(CancellationToken cancellationToken)
    {
        if (_currentUserContext.UserId is not { } userId)
        {
            return (null, Err(ErrorCodes.Unauthenticated, "Current user is not authenticated."));
        }

        var user = await _userAuthStore.FindByIdAsync(userId, cancellationToken);
        if (user is null || user.DeletedAt is not null)
        {
            return (null, Err(ErrorCodes.Unauthenticated, "Current user is not authenticated."));
        }

        return user.IsBlocked ? (null, Blocked()) : (user, null);
    }

    private static ServiceError Blocked() => Err(ErrorCodes.UserBlocked, "User account is blocked.");

    private static ServiceError Err(string code, string message, int? retryAfterSeconds = null) =>
        new() { Code = code, Message = message, RetryAfterSeconds = retryAfterSeconds };

    private static ServiceResult<CurrentUserResponse> Fail(ServiceError error) => ServiceResult<CurrentUserResponse>.Failure(error);

    private static ServiceResult<CurrentUserResponse> Fail(string code, string message, int? retryAfterSeconds = null) =>
        Fail(Err(code, message, retryAfterSeconds));
}
