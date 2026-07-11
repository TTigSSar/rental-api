using Microsoft.AspNetCore.Http;

namespace RentalPlatform.Api.Controllers.Requests;

public sealed class UploadChatImageRequest
{
    public required IFormFile Image { get; init; }

    /// <summary>Optional caption accompanying the image.</summary>
    public string? Caption { get; init; }
}
