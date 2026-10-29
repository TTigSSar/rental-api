using Xunit;
using RentalPlatform.Application.Common;

namespace RentalPlatform.Tests.Common;

public class AiSearchRankValidatorTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid C = Guid.NewGuid();

    [Fact]
    public void Follows_The_Model_Order_When_It_Is_Complete()
    {
        Assert.Equal(new[] { C, A, B }, AiSearchRankValidator.Merge(new[] { A, B, C }, new[] { C, A, B }));
    }

    [Fact]
    public void Drops_Unknown_And_Repeated_Ids()
    {
        var result = AiSearchRankValidator.Merge(new[] { A, B }, new[] { B, Guid.NewGuid(), B, A });
        Assert.Equal(new[] { B, A }, result);
    }

    [Fact]
    public void Appends_Missing_Candidates_In_Fallback_Order()
    {
        var result = AiSearchRankValidator.Merge(new[] { A, B, C }, new[] { C });
        Assert.Equal(new[] { C, A, B }, result);
    }

    [Fact]
    public void Null_Model_Order_Returns_The_Fallback_Order()
    {
        Assert.Equal(new[] { A, B, C }, AiSearchRankValidator.Merge(new[] { A, B, C }, null));
    }
}
