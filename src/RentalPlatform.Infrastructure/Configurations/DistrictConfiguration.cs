using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Infrastructure.Configurations;

public sealed class DistrictConfiguration : IEntityTypeConfiguration<District>
{
    public void Configure(EntityTypeBuilder<District> builder)
    {
        builder.ToTable("Districts");

        builder.HasKey(district => district.Id);

        builder.Property(district => district.Code)
            .IsRequired()
            .HasMaxLength(64);

        builder.HasIndex(district => district.Code)
            .IsUnique();

        builder.Property(district => district.NameEn)
            .IsRequired()
            .HasMaxLength(120);

        builder.Property(district => district.NameHy)
            .IsRequired()
            .HasMaxLength(120);

        builder.Property(district => district.NameRu)
            .IsRequired()
            .HasMaxLength(120);

        // The 12 Yerevan districts, reference data every environment needs (not demo data), so —
        // like SeedReferenceCategories — it ships via migration rather than only the
        // Development-only seed. Unlike that migration, this table is brand new (nothing has ever
        // written a Districts row anywhere, dev seed included — verified: no seed code references
        // "District"), so there is no pre-existing-row conflict to work around, and EF's ordinary
        // HasData seeding (InsertData on Up / DeleteData on Down) is safe and simpler than a
        // hand-written idempotent MERGE. Guids are hard-coded and stable (never regenerate them)
        // so the same 12 rows land with the same Ids in every environment. Values are copied
        // byte-for-byte from Infrastructure/Resources/yerevan-districts.geojson's `code`/`nameEn`/
        // `nameHy`/`nameRu` properties, in the order those features appear in that file.
        builder.HasData(
            new District { Id = new Guid("d0000001-0000-4000-9000-000000000001"), Code = "ajapnyak", NameEn = "Ajapnyak", NameHy = "Աջափնյակ", NameRu = "Ачапняк" },
            new District { Id = new Guid("d0000002-0000-4000-9000-000000000002"), Code = "arabkir", NameEn = "Arabkir", NameHy = "Արաբկիր", NameRu = "Арабкир" },
            new District { Id = new Guid("d0000003-0000-4000-9000-000000000003"), Code = "avan", NameEn = "Avan", NameHy = "Ավան", NameRu = "Аван" },
            new District { Id = new Guid("d0000004-0000-4000-9000-000000000004"), Code = "davtashen", NameEn = "Davtashen", NameHy = "Դավթաշեն", NameRu = "Давташен" },
            new District { Id = new Guid("d0000005-0000-4000-9000-000000000005"), Code = "erebuni", NameEn = "Erebuni", NameHy = "Էրեբունի", NameRu = "Эребуни" },
            new District { Id = new Guid("d0000006-0000-4000-9000-000000000006"), Code = "kanaker-zeytun", NameEn = "Kanaker-Zeytun", NameHy = "Քանաքեռ-Զեյթուն", NameRu = "Канакер-Зейтун" },
            new District { Id = new Guid("d0000007-0000-4000-9000-000000000007"), Code = "kentron", NameEn = "Kentron", NameHy = "Կենտրոն", NameRu = "Кентрон" },
            new District { Id = new Guid("d0000008-0000-4000-9000-000000000008"), Code = "malatia-sebastia", NameEn = "Malatia-Sebastia", NameHy = "Մալաթիա-Սեբաստիա", NameRu = "Малатия-Себастия" },
            new District { Id = new Guid("d0000009-0000-4000-9000-000000000009"), Code = "nork-marash", NameEn = "Nork-Marash", NameHy = "Նորք Մարաշ", NameRu = "Норк Мараш" },
            new District { Id = new Guid("d000000a-0000-4000-9000-00000000000a"), Code = "nor-nork", NameEn = "Nor Nork", NameHy = "Նոր Նորք", NameRu = "Нор Норк" },
            new District { Id = new Guid("d000000b-0000-4000-9000-00000000000b"), Code = "nubarashen", NameEn = "Nubarashen", NameHy = "Նուբարաշեն", NameRu = "Нубарашен" },
            new District { Id = new Guid("d000000c-0000-4000-9000-00000000000c"), Code = "shengavit", NameEn = "Shengavit", NameHy = "Շենգավիթ", NameRu = "Шенгавит" }
        );
    }
}
