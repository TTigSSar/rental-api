namespace RentalPlatform.Domain.Entities;

public sealed class ListingImage
{
    public Guid Id { get; set; }
    public Guid ListingId { get; set; }
    public string Url { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }
    public int SortOrder { get; set; }

    public Listing Listing { get; set; } = null!;
}
