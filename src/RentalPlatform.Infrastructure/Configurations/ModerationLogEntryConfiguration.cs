using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Configurations;

public sealed class ModerationLogEntryConfiguration : IEntityTypeConfiguration<ModerationLogEntry>
{
    public void Configure(EntityTypeBuilder<ModerationLogEntry> builder)
    {
        builder.ToTable("ModerationLogEntries");

        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.Action).IsRequired();
        builder.Property(entry => entry.TargetType).IsRequired();
        builder.Property(entry => entry.TargetLabel).IsRequired().HasMaxLength(300);
        builder.Property(entry => entry.DetailJson).HasMaxLength(2000);
        builder.Property(entry => entry.CreatedAt).IsRequired();

        // Activity feed reads newest-first. A plain (ascending) B-tree index on CreatedAt is
        // scanned efficiently in either direction by SQL Server, so this serves an ORDER BY
        // CreatedAt DESC query just as well as a descending index would — used instead of the
        // fluent .IsDescending() API because that API could not be verified against current
        // EF Core 8 docs in this session (context7 MCP was unavailable; see final report).
        builder.HasIndex(entry => entry.CreatedAt);

        // Target-scoped lookups (e.g. "history for this listing") filter on both columns together.
        builder.HasIndex(entry => new { entry.TargetType, entry.TargetId });

        builder.HasOne(entry => entry.Actor)
            .WithMany()
            .HasForeignKey(entry => entry.ActorUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
