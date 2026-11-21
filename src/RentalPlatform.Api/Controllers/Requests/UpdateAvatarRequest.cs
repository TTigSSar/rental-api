using Microsoft.AspNetCore.Http;

namespace RentalPlatform.Api.Controllers.Requests;

// PUT /api/auth/me/avatar. The file is optional at binding level on purpose: a missing file must answer
// 400 auth.avatar_missing with an errorCode, not the bare model-state 400 a required member would give.
public sealed class UpdateAvatarRequest
{
    public IFormFile? File { get; init; }
}
