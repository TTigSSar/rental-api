using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Configurations;

// No foreign keys on purpose (a category may be hard-deleted; counts keep the old id).
public sealed class DemandDailyCountConfiguration : IEntityTypeConfiguration<DemandDailyCount>
{
    public void Configure(EntityTypeBuilder<DemandDailyCount> builder)
    {
        builder.ToTable("DemandDailyCounts");

        // Non-clustered: the clustered index is the natural key below, which avoids random NEWID() page
        // splits on a table that is kept forever and gives the increment a seek.
        builder.HasKey(count => count.Id).IsClustered(false);

        builder.Property(count => count.Id).ValueGeneratedNever();
        builder.Property(count => count.Day).HasColumnType("date").IsRequired();
        builder.Property(count => count.CategoryId).IsRequired();
        builder.Property(count => count.BookingRequests).IsRequired();

        // HasFilter(null) is load-bearing: without it the SQL Server provider adds
        // "WHERE [DistrictId] IS NOT NULL" to a unique index over a nullable column, and rows with a
        // NULL district could then duplicate.
        builder.HasIndex(count => new { count.Day, count.DistrictId, count.CategoryId })
            .IsUnique()
            .IsClustered()
            .HasFilter(null);
    }
}
