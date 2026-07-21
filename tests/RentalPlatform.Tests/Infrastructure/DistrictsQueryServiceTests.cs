using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using Xunit;

namespace RentalPlatform.Tests.Infrastructure;

// Backs GET /api/districts (DistrictsController) — the reference-data endpoint the upcoming
// create-listing district picker (P1-6) populates its select from. Districts are seeded via
// migration (DistrictConfiguration.HasData), so EnsureCreated on the Sqlite test database already
// carries all 12 rows; this test only guards the query service's mapping/ordering contract.
public sealed class DistrictsQueryServiceTests
{
    [Fact]
    public async Task GetAllAsync_Returns_All_12_Districts_Ordered_By_NameEn_With_Every_Field_Populated()
    {
        using var database = new SqliteTestDatabase();
        await using var context = database.CreateContext();
        var service = new DistrictsQueryService(context);

        var districts = await service.GetAllAsync();

        Assert.Equal(12, districts.Count);
        Assert.All(districts, district =>
        {
            Assert.NotEqual(Guid.Empty, district.Id);
            Assert.False(string.IsNullOrWhiteSpace(district.Code));
            Assert.False(string.IsNullOrWhiteSpace(district.NameEn));
            Assert.False(string.IsNullOrWhiteSpace(district.NameHy));
            Assert.False(string.IsNullOrWhiteSpace(district.NameRu));
        });

        var expectedOrder = districts.Select(d => d.NameEn).OrderBy(name => name, StringComparer.Ordinal).ToList();
        Assert.Equal(expectedOrder, districts.Select(d => d.NameEn).ToList());
    }
}
