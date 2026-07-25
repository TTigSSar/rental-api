using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Tests.TestSupport;

// Minimal test double for IExternalIdentityTokenValidator — always fails validation. Sufficient
// for tests that only need to construct AuthService but don't exercise the external-auth flow.
public sealed class FakeExternalIdentityTokenValidator : IExternalIdentityTokenValidator
{
    public Task<ServiceResult<ExternalUserInfo>> ValidateAsync(
        string provider,
        string idToken,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(ServiceResult<ExternalUserInfo>.Failure(new ServiceError
        {
            Code = "auth.external_invalid_token",
            Message = "Not supported by FakeExternalIdentityTokenValidator."
        }));
}
