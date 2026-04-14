using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Domain.Entities;

public sealed class Listing
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid CategoryId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal PricePerDay { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string AddressLine { get; set; } = string.Empty;
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public ListingStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User Owner { get; set; } = null!;
    public Category Category { get; set; } = null!;
    public ICollection<ListingImage> Images { get; set; } = new List<ListingImage>();
}
