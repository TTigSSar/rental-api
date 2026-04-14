namespace RentalPlatform.Infrastructure.Services;

public sealed class LocalFileStorageOptions
{
    public const string SectionName = "FileStorage";

    public string ListingsImagesPath { get; init; } = "uploads/listings";
}
