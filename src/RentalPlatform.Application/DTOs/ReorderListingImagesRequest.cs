namespace RentalPlatform.Application.DTOs;

public sealed class ReorderListingImagesRequest
{
    /// <summary>
    /// Image IDs in the desired display order. The first ID becomes primary.
    /// Must contain exactly the current set of image IDs for the listing.
    /// </summary>
    public IReadOnlyList<Guid> ImageIds { get; init; } = [];
}
