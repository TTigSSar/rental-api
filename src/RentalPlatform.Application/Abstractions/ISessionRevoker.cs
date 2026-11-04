namespace RentalPlatform.Application.Abstractions;

/// <summary>
/// Ends sessions after the database state that invalidates them has COMMITTED (ADR-055). Never throws:
/// a failure here must not fail the operation that already succeeded.
/// </summary>
public interface ISessionRevoker
{
    /// <summary>
    /// After a TokenVersion rotation (password change, reset): drops the user's cached security
    /// state, then best-effort aborts the user's open chat connections.
    /// </summary>
    Task RevokeAsync(Guid userId);
}
