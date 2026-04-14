namespace RentalPlatform.Application.Abstractions;

public interface IFileStorageService
{
    Task<string> SaveListingImageAsync(
        Stream content,
        string fileName,
        string contentType,
        CancellationToken cancellationToken = default);
}
