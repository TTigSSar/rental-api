using RentalPlatform.Domain.ValueObjects;
using Xunit;

namespace RentalPlatform.Tests.Domain;

public sealed class GeoCoordinateTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(40.1776, 44.5126)] // Yerevan, Republic Square
    [InlineData(-90, -180)]
    [InlineData(90, 180)]
    [InlineData(-90, 180)]
    [InlineData(90, -180)]
    public void Constructor_Accepts_Valid_And_Boundary_Values(decimal latitude, decimal longitude)
    {
        var coordinate = new GeoCoordinate(latitude, longitude);

        Assert.Equal(latitude, coordinate.Latitude);
        Assert.Equal(longitude, coordinate.Longitude);
    }

    [Theory]
    [InlineData(90.0000001, 0)]
    [InlineData(-90.0000001, 0)]
    [InlineData(91, 0)]
    [InlineData(-91, 0)]
    public void Constructor_Rejects_Latitude_Outside_Range(decimal latitude, decimal longitude)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeoCoordinate(latitude, longitude));
    }

    [Theory]
    [InlineData(0, 180.0000001)]
    [InlineData(0, -180.0000001)]
    [InlineData(0, 181)]
    [InlineData(0, -181)]
    public void Constructor_Rejects_Longitude_Outside_Range(decimal latitude, decimal longitude)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeoCoordinate(latitude, longitude));
    }

    // There is no API surface on GeoCoordinate that allows setting one coordinate without the
    // other — the only constructor requires both values simultaneously — so a "half-set"
    // coordinate cannot be constructed at all. This test locks in that shape: an invalid latitude
    // fails before a caller could ever obtain an instance holding only a longitude, and vice
    // versa.
    [Fact]
    public void Constructor_Rejects_Invalid_Latitude_Even_With_Valid_Longitude_Leaving_No_Partial_Instance()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new GeoCoordinate(999, 44.5126m));
        Assert.Equal("latitude", exception.ParamName);
    }

    [Fact]
    public void Constructor_Rejects_Invalid_Longitude_Even_With_Valid_Latitude_Leaving_No_Partial_Instance()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new GeoCoordinate(40.1776m, 999));
        Assert.Equal("longitude", exception.ParamName);
    }

    [Fact]
    public void Equality_Is_Structural_Not_Reference()
    {
        var a = new GeoCoordinate(40.1776m, 44.5126m);
        var b = new GeoCoordinate(40.1776m, 44.5126m);

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equality_Distinguishes_Different_Values()
    {
        var a = new GeoCoordinate(40.1776m, 44.5126m);
        var b = new GeoCoordinate(40.1777m, 44.5126m);

        Assert.NotEqual(a, b);
        Assert.False(a == b);
    }
}
