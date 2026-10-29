using Xunit;
using RentalPlatform.Application.Common;

namespace RentalPlatform.Tests.Common;

public class YerevanDistrictAdjacencyTests
{
    [Fact]
    public void Covers_All_Twelve_District_Codes()
    {
        Assert.Equal(12, YerevanDistrictAdjacency.Codes.Count);
    }

    [Fact]
    public void Is_Symmetric_And_Has_No_Self_Or_Unknown_Neighbours()
    {
        var codes = YerevanDistrictAdjacency.Codes.ToHashSet();
        foreach (var code in codes)
        {
            var neighbours = YerevanDistrictAdjacency.NeighboursOf(code);
            Assert.NotEmpty(neighbours);
            Assert.DoesNotContain(code, neighbours);
            foreach (var n in neighbours)
            {
                Assert.Contains(n, codes);
                Assert.Contains(code, YerevanDistrictAdjacency.NeighboursOf(n));
            }
        }
    }

    [Fact]
    public void Unknown_Code_Has_No_Neighbours()
    {
        Assert.Empty(YerevanDistrictAdjacency.NeighboursOf("gyumri"));
    }
}
