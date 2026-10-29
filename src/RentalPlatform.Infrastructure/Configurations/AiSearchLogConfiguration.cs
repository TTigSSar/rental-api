using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Configurations;

public sealed class AiSearchLogConfiguration : IEntityTypeConfiguration<AiSearchLog>
{
    public void Configure(EntityTypeBuilder<AiSearchLog> builder)
    {
        builder.ToTable("AiSearchLogs");

        builder.HasKey(log => log.Id);

        builder.Property(log => log.CreatedAt).IsRequired();
        builder.Property(log => log.Description).HasMaxLength(500);
        builder.Property(log => log.ParseModel).HasMaxLength(100);
        builder.Property(log => log.RankModel).HasMaxLength(100);
        builder.Property(log => log.CostUsd).HasColumnType("decimal(10,6)");
        builder.Property(log => log.Outcome).IsRequired().HasMaxLength(30);

        // Budget sum and the 30-day cleanup both range-scan on CreatedAt.
        builder.HasIndex(log => log.CreatedAt);

        builder.HasOne(log => log.User)
            .WithMany()
            .HasForeignKey(log => log.UserId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
