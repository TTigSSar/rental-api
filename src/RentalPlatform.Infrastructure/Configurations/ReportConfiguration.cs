using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Configurations;

public sealed class ReportConfiguration : IEntityTypeConfiguration<Report>
{
    public void Configure(EntityTypeBuilder<Report> builder)
    {
        builder.ToTable("Reports");

        builder.HasKey(report => report.Id);

        builder.Property(report => report.TargetType).IsRequired();
        builder.Property(report => report.TargetLabel).IsRequired().HasMaxLength(300);
        builder.Property(report => report.ReasonCode).IsRequired().HasMaxLength(64);
        builder.Property(report => report.Detail).HasMaxLength(2000);
        builder.Property(report => report.Severity).IsRequired();
        builder.Property(report => report.Status).IsRequired();
        builder.Property(report => report.CreatedAt).IsRequired();
        builder.Property(report => report.ResolutionNote).HasMaxLength(1000);

        // Admin queue's default sort (severity desc, then oldest first) filters by status first,
        // so (Status, CreatedAt) serves that predicate + tiebreaker together.
        builder.HasIndex(report => new { report.Status, report.CreatedAt });

        // "History for this target" / duplicate-open-report lookups filter on both columns together.
        builder.HasIndex(report => new { report.TargetType, report.TargetId });

        builder.HasIndex(report => report.ReporterUserId);

        // Same convention as ModerationLogEntryConfiguration: Restrict, not Cascade — a user row
        // must not be deletable out from under its reports (filed or resolved).
        builder.HasOne(report => report.Reporter)
            .WithMany()
            .HasForeignKey(report => report.ReporterUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(report => report.ResolvedByUser)
            .WithMany()
            .HasForeignKey(report => report.ResolvedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
