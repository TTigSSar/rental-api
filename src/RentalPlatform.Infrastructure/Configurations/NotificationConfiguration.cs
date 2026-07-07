using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Configurations;

public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("Notifications");

        builder.HasKey(notification => notification.Id);

        builder.Property(notification => notification.Kind).IsRequired();
        builder.Property(notification => notification.Category).IsRequired();

        builder.Property(notification => notification.Title).IsRequired().HasMaxLength(200);
        builder.Property(notification => notification.Body).IsRequired().HasMaxLength(1000);
        builder.Property(notification => notification.Meta).HasMaxLength(200);

        builder.Property(notification => notification.ActorName).IsRequired().HasMaxLength(200);
        builder.Property(notification => notification.ActorAvatarUrl).HasMaxLength(2048);
        builder.Property(notification => notification.ActorSystemIcon).HasMaxLength(64);

        builder.Property(notification => notification.DeepLink).IsRequired().HasMaxLength(2048);
        builder.Property(notification => notification.ToyTitle).HasMaxLength(200);
        builder.Property(notification => notification.ToyImageUrl).HasMaxLength(2048);

        builder.Property(notification => notification.PrimaryActionLabel).HasMaxLength(120);
        builder.Property(notification => notification.PrimaryActionDeepLink).HasMaxLength(2048);
        builder.Property(notification => notification.SecondaryActionLabel).HasMaxLength(120);
        builder.Property(notification => notification.SecondaryActionDeepLink).HasMaxLength(2048);

        builder.Property(notification => notification.CreatedAt).IsRequired();

        // Feed page + unread/action counts all filter by recipient and order by
        // recency; this composite index covers both access paths.
        builder.HasIndex(notification => new
        {
            notification.RecipientId,
            notification.ReadAt,
            notification.CreatedAt
        });

        builder.HasOne(notification => notification.Recipient)
            .WithMany()
            .HasForeignKey(notification => notification.RecipientId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
