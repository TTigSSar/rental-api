namespace RentalPlatform.Application.Abstractions;

/// <summary>
/// Ends sessions after the database state that invalidates them has COMMITTED (ADR-055). Never throws:
/// a failure here must not fail the operation that already succeeded.
/// </summary>
public interface ISessionRevoker
{
    /// <summary>
    /// After a TokenVersion rotation (password change, reset, block): drops the user's cached security
    /// state, then best-effort aborts the user's open chat connections.
    /// </summary>
    Task RevokeAsync(Guid userId);

    /// <summary>
    /// After a change that does not rotate the version (unblock): drops the cached security state only,
    /// so the next request sees the new <c>IsBlocked</c> at once instead of up to 60 s later.
    /// </summary>
    Task InvalidateAsync(Guid userId);
}
