using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Configurations;

public sealed class PushSubscriptionConfiguration : IEntityTypeConfiguration<PushSubscription>
{
    public void Configure(EntityTypeBuilder<PushSubscription> builder)
    {
        builder.ToTable("PushSubscriptions");

        builder.HasKey(subscription => subscription.Id);

        builder.Property(subscription => subscription.Endpoint)
            .IsRequired()
            .HasMaxLength(2048);

        // SHA-256 digest: binary(32) on SQL Server.
        builder.Property(subscription => subscription.EndpointHash)
            .IsRequired()
            .HasMaxLength(32)
            .IsFixedLength();

        builder.Property(subscription => subscription.P256dh)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(subscription => subscription.Auth)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(subscription => subscription.CreatedAt).IsRequired();

        builder.HasIndex(subscription => subscription.EndpointHash)
            .IsUnique();

        builder.HasIndex(subscription => subscription.UserId);

        builder.HasOne(subscription => subscription.User)
            .WithMany()
            .HasForeignKey(subscription => subscription.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
