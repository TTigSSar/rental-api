using RentalPlatform.Domain.ValueObjects;
using Xunit;

namespace RentalPlatform.Tests.Domain;

public sealed class AddressTests
{
    [Fact]
    public void Constructor_Accepts_Valid_City_With_Optional_Parts_Omitted()
    {
        var address = new Address("Yerevan", null, null);

        Assert.Equal("Yerevan", address.City);
        Assert.Null(address.District);
        Assert.Null(address.AddressLine);
    }

    [Fact]
    public void Constructor_Accepts_All_Parts_Populated()
    {
        var address = new Address("Yerevan", "Kentron", "1 Republic Square");

        Assert.Equal("Yerevan", address.City);
        Assert.Equal("Kentron", address.District);
        Assert.Equal("1 Republic Square", address.AddressLine);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_Rejects_Missing_City(string? city)
    {
        Assert.Throws<ArgumentException>(() => new Address(city!, "Kentron", "1 Republic Square"));
    }

    [Fact]
    public void Equality_Is_Structural_Not_Reference()
    {
        var a = new Address("Yerevan", "Kentron", "1 Republic Square");
        var b = new Address("Yerevan", "Kentron", "1 Republic Square");

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equality_Distinguishes_Different_Values()
    {
        var a = new Address("Yerevan", "Kentron", "1 Republic Square");
        var b = new Address("Yerevan", "Arabkir", "1 Republic Square");

        Assert.NotEqual(a, b);
        Assert.False(a == b);
    }
}
