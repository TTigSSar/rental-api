using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Configurations;

public sealed class UserTokenConfiguration : IEntityTypeConfiguration<UserToken>
{
    public void Configure(EntityTypeBuilder<UserToken> builder)
    {
        builder.ToTable("UserTokens");

        builder.HasKey(token => token.Id);

        builder.Property(token => token.Purpose)
            .IsRequired();

        // SHA-256 digest: binary(32) on SQL Server.
        builder.Property(token => token.TokenHash)
            .IsRequired()
            .HasMaxLength(32)
            .IsFixedLength();

        builder.Property(token => token.ExpiresAt).IsRequired();
        builder.Property(token => token.CreatedAt).IsRequired();

        builder.HasIndex(token => token.TokenHash)
            .IsUnique();

        // At most one ACTIVE token per user and purpose. A revoked/consumed token leaves the index.
        // Bracket syntax, like UserConfiguration's external-identity index, works on SQLite too.
        builder.HasIndex(token => new { token.UserId, token.Purpose })
            .IsUnique()
            .HasFilter("[ConsumedAt] IS NULL");

        builder.HasOne(token => token.User)
            .WithMany()
            .HasForeignKey(token => token.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
