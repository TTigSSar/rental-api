using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Configurations;

public sealed class ListingConfiguration : IEntityTypeConfiguration<Listing>
{
    public void Configure(EntityTypeBuilder<Listing> builder)
    {
        builder.ToTable("Listings");

        builder.HasKey(listing => listing.Id);

        builder.Property(listing => listing.Title)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(listing => listing.Description)
            .IsRequired()
            .HasMaxLength(4000);

        builder.Property(listing => listing.PricePerDay)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(listing => listing.Currency)
            .IsRequired()
            .HasMaxLength(3);

        builder.Property(listing => listing.Country)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(listing => listing.City)
            .IsRequired()
            .HasMaxLength(120);

        builder.Property(listing => listing.AddressLine)
            .IsRequired()
            .HasMaxLength(250);

        builder.Property(listing => listing.Latitude)
            .HasPrecision(9, 6)
            .IsRequired();

        builder.Property(listing => listing.Longitude)
            .HasPrecision(9, 6)
            .IsRequired();

        builder.Property(listing => listing.Status)
            .IsRequired();

        builder.Property(listing => listing.CreatedAt)
            .IsRequired();

        builder.Property(listing => listing.UpdatedAt)
            .IsRequired();

        builder.HasOne(listing => listing.Owner)
            .WithMany(user => user.Listings)
            .HasForeignKey(listing => listing.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(listing => listing.Category)
            .WithMany(category => category.Listings)
            .HasForeignKey(listing => listing.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(listing => listing.Images)
            .WithOne(image => image.Listing)
            .HasForeignKey(image => image.ListingId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(listing => listing.Status);
        builder.HasIndex(listing => listing.City);
        builder.HasIndex(listing => listing.CategoryId);
    }
}
