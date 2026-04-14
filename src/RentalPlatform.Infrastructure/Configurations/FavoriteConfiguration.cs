using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Configurations;

public sealed class FavoriteConfiguration : IEntityTypeConfiguration<Favorite>
{
    public void Configure(EntityTypeBuilder<Favorite> builder)
    {
        builder.ToTable("Favorites");

        builder.HasKey(favorite => favorite.Id);

        builder.Property(favorite => favorite.CreatedAt)
            .IsRequired();

        builder.HasIndex(favorite => new { favorite.UserId, favorite.ListingId })
            .IsUnique();

        builder.HasOne(favorite => favorite.User)
            .WithMany(user => user.Favorites)
            .HasForeignKey(favorite => favorite.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(favorite => favorite.Listing)
            .WithMany(listing => listing.Favorites)
            .HasForeignKey(favorite => favorite.ListingId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
