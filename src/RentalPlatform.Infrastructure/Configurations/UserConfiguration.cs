using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");

        builder.HasKey(user => user.Id);

        builder.Property(user => user.Email)
            .IsRequired()
            .HasMaxLength(320);

        builder.HasIndex(user => user.Email)
            .IsUnique();

        builder.Property(user => user.PasswordHash)
            .IsRequired()
            .HasMaxLength(512);

        builder.Property(user => user.FirstName)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(user => user.LastName)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(user => user.PhoneNumber)
            .HasMaxLength(32);

        builder.Property(user => user.PreferredLanguage)
            .HasMaxLength(16);

        builder.Property(user => user.ExternalAuthProvider)
            .HasMaxLength(32);

        builder.Property(user => user.ExternalProviderId)
            .HasMaxLength(256);

        builder.Property(user => user.AvatarUrl)
            .HasMaxLength(1000);

        builder.HasIndex(user => new { user.ExternalAuthProvider, user.ExternalProviderId })
            .IsUnique()
            .HasFilter("[ExternalAuthProvider] IS NOT NULL AND [ExternalProviderId] IS NOT NULL");

        builder.Property(user => user.CreatedAt)
            .IsRequired();

        builder.Property(user => user.IsBlocked)
            .IsRequired();

        builder.Property(user => user.Role)
            .IsRequired();

        // Home point (home-point model). Same precision/scale as the listing location columns (ADR-008).
        builder.Property(user => user.HomeLatitude)
            .HasPrecision(9, 6);

        builder.Property(user => user.HomeLongitude)
            .HasPrecision(9, 6);

        builder.Property(user => user.HomePublicLatitude)
            .HasPrecision(9, 6);

        builder.Property(user => user.HomePublicLongitude)
            .HasPrecision(9, 6);

        builder.HasOne(user => user.HomeDistrict)
            .WithMany()
            .HasForeignKey(user => user.HomeDistrictId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(user => user.HomeDistrictId);
    }
}
