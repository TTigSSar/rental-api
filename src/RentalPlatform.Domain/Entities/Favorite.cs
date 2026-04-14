namespace RentalPlatform.Domain.Entities;

public sealed class Favorite
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid ListingId { get; set; }
    public DateTime CreatedAt { get; set; }

    public User User { get; set; } = null!;
    public Listing Listing { get; set; } = null!;
}
