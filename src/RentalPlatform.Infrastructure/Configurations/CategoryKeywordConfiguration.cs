using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Configurations;

public sealed class CategoryKeywordConfiguration : IEntityTypeConfiguration<CategoryKeyword>
{
    public void Configure(EntityTypeBuilder<CategoryKeyword> builder)
    {
        builder.ToTable("CategoryKeywords");

        builder.HasKey(keyword => keyword.Id);

        builder.Property(keyword => keyword.Keyword)
            .IsRequired()
            .HasMaxLength(64);

        // A given word/phrase maps to a category at most once; the plain Keyword index backs the
        // suggestion rule's "load the whole small table once per request" read.
        builder.HasIndex(keyword => new { keyword.CategoryId, keyword.Keyword })
            .IsUnique();
        builder.HasIndex(keyword => keyword.Keyword);

        builder.HasOne(keyword => keyword.Category)
            .WithMany()
            .HasForeignKey(keyword => keyword.CategoryId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
