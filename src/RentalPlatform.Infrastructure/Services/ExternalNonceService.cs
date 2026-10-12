using Microsoft.Extensions.Options;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;

namespace RentalPlatform.Infrastructure.Services;

public sealed class ExternalNonceService : IExternalNonceService
{
    private readonly ExternalAuthOptions _options;
    private readonly ExternalAuthNonceStore _store;
    private readonly TimeProvider _timeProvider;

    public ExternalNonceService(
        IOptions<ExternalAuthOptions> options,
        ExternalAuthNonceStore store,
        TimeProvider timeProvider)
    {
        _options = options.Value;
        _store = store;
        _timeProvider = timeProvider;
    }

    public ServiceResult<ExternalNonceResponse> Issue()
    {
        // No client id means no token could ever be accepted, so do not hand out nonces for it.
        var issued = _options.Google.IsConfigured ? _store.TryIssue(_timeProvider.GetUtcNow()) : null;
        if (issued is null)
        {
            return ServiceResult<ExternalNonceResponse>.Failure(new ServiceError
            {
                Code = "auth.external_provider_unavailable",
                Message = "Google sign-in is not available."
            });
        }

        return ServiceResult<ExternalNonceResponse>.Success(new ExternalNonceResponse
        {
            Nonce = issued.Nonce,
            ExpiresAt = issued.ExpiresAt.UtcDateTime
        });
    }
}
