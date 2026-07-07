using System.Text.Json;
using RentalPlatform.Api.Serialization;
using Xunit;

namespace RentalPlatform.Tests.Api;

public class UtcDateTimeJsonConverterTests
{
    private static JsonSerializerOptions BuildOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new UtcDateTimeJsonConverter());
        options.Converters.Add(new UtcNullableDateTimeJsonConverter());
        return options;
    }

    [Fact]
    public void Write_UnspecifiedKind_EmitsUtcSuffix()
    {
        // Simulates a value read back from SQL Server datetime2, which EF Core surfaces as Unspecified
        // even though the application always writes DateTime.UtcNow.
        var value = new DateTime(2026, 7, 7, 18, 13, 0, DateTimeKind.Unspecified).AddTicks(8_900_000);
        var options = BuildOptions();

        var json = JsonSerializer.Serialize(value, options);

        Assert.EndsWith("Z\"", json);
    }

    [Fact]
    public void Write_LocalKind_ConvertsToUtcAndEmitsUtcSuffix()
    {
        var value = DateTime.SpecifyKind(new DateTime(2026, 7, 7, 22, 13, 0), DateTimeKind.Local);
        var options = BuildOptions();

        var json = JsonSerializer.Serialize(value, options);

        Assert.EndsWith("Z\"", json);
    }

    [Fact]
    public void Write_UtcKind_EmitsUtcSuffix()
    {
        var value = new DateTime(2026, 7, 7, 18, 13, 0, DateTimeKind.Utc);
        var options = BuildOptions();

        var json = JsonSerializer.Serialize(value, options);

        Assert.EndsWith("Z\"", json);
    }

    [Fact]
    public void Write_NullableDateTime_Null_EmitsJsonNull()
    {
        DateTime? value = null;
        var options = BuildOptions();

        var json = JsonSerializer.Serialize(value, options);

        Assert.Equal("null", json);
    }

    [Fact]
    public void Write_NullableDateTime_WithValue_EmitsUtcSuffix()
    {
        DateTime? value = new DateTime(2026, 7, 7, 18, 13, 0, DateTimeKind.Unspecified);
        var options = BuildOptions();

        var json = JsonSerializer.Serialize(value, options);

        Assert.EndsWith("Z\"", json);
    }

    [Fact]
    public void ReadThenWrite_RoundTripsStablyAsUtc()
    {
        const string original = "\"2026-07-07T18:13:00.89Z\"";
        var options = BuildOptions();

        var parsed = JsonSerializer.Deserialize<DateTime>(original, options);
        Assert.Equal(DateTimeKind.Utc, parsed.Kind);

        var roundTripped = JsonSerializer.Serialize(parsed, options);

        Assert.EndsWith("Z\"", roundTripped);
    }
}
