using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Application.Abstractions;

/// <summary>
/// Issues the single-use nonce that the SPA hands to Google Identity Services (ADR-030 section 2).
/// Fails with auth.external_provider_unavailable when Google is not configured or the store is full.
/// </summary>
public interface IExternalNonceService
{
    ServiceResult<ExternalNonceResponse> Issue();
}
