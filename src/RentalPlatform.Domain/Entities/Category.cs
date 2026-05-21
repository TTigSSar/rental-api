namespace RentalPlatform.Domain.Entities;

public sealed class Category
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? IconName { get; set; }
    public string? ImageUrl { get; set; }
    public int DisplayOrder { get; set; }

    public ICollection<Listing> Listings { get; set; } = new List<Listing>();
}
