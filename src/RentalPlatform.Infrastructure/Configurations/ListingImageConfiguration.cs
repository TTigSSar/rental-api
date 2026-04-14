using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Configurations;

public sealed class ListingImageConfiguration : IEntityTypeConfiguration<ListingImage>
{
    public void Configure(EntityTypeBuilder<ListingImage> builder)
    {
        builder.ToTable("ListingImages");

        builder.HasKey(image => image.Id);

        builder.Property(image => image.Url)
            .IsRequired()
            .HasMaxLength(1000);

        builder.Property(image => image.IsPrimary)
            .IsRequired();

        builder.Property(image => image.SortOrder)
            .IsRequired();

        builder.HasIndex(image => new { image.ListingId, image.SortOrder });
    }
}
