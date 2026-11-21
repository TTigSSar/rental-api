namespace RentalPlatform.Infrastructure.Services;

public sealed class LocalFileStorageOptions
{
    public const string SectionName = "FileStorage";

    public string ListingsImagesPath { get; init; } = "uploads/listings";

    public string ChatAttachmentsPath { get; init; } = "uploads/chat";

    // ADR-070: own volume (rental-api_avatar-uploads), layout {AvatarsPath}/{userId:N}/{guid:N}.jpg.
    public string AvatarsPath { get; init; } = "uploads/avatars";
}
