using Microsoft.AspNetCore.Http;

namespace RentalPlatform.Api.Controllers.Requests;

public sealed class UploadListingImagesRequest
{
    public List<IFormFile> Files { get; init; } = new();
}
