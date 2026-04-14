using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Configurations;

public sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("Categories");

        builder.HasKey(category => category.Id);

        builder.Property(category => category.Name)
            .IsRequired()
            .HasMaxLength(120);

        builder.Property(category => category.Slug)
            .IsRequired()
            .HasMaxLength(140);

        builder.HasIndex(category => category.Slug)
            .IsUnique();
    }
}
