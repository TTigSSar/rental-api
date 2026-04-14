namespace RentalPlatform.Application.DTOs;

public sealed class UploadListingImageRequest
{
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required long Length { get; init; }
    public required Stream Content { get; init; }
}
