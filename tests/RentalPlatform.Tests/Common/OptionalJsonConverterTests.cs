using System.Text.Json;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using Xunit;

namespace RentalPlatform.Tests.Common;

public sealed class OptionalJsonConverterTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Round_Trip_Omits_Unset_And_Preserves_Set_And_Explicit_Null()
    {
        var request = new UpdateListingRequest
        {
            AgeFromMonths = new Optional<int?>(12),
            Condition = new Optional<string?>(null),
            MinRentalDays = new Optional<int?>(3)
        };

        var json = JsonSerializer.Serialize(request, Web);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("ageFromMonths", out _));
        Assert.True(root.TryGetProperty("condition", out var condition));
        Assert.Equal(JsonValueKind.Null, condition.ValueKind);
        Assert.True(root.TryGetProperty("minRentalDays", out _));
        Assert.False(root.TryGetProperty("ageToMonths", out _));
        Assert.False(root.TryGetProperty("hygieneNotes", out _));
        Assert.False(root.TryGetProperty("safetyNotes", out _));
        Assert.False(root.TryGetProperty("compensationAmount", out _));

        var back = JsonSerializer.Deserialize<UpdateListingRequest>(json, Web)!;
        Assert.True(back.AgeFromMonths.IsSet);
        Assert.Equal(12, back.AgeFromMonths.Value);
        Assert.True(back.Condition.IsSet);
        Assert.Null(back.Condition.Value);
        Assert.True(back.MinRentalDays.IsSet);
        Assert.False(back.AgeToMonths.IsSet);
        Assert.False(back.HygieneNotes.IsSet);
        Assert.False(back.SafetyNotes.IsSet);
        Assert.False(back.CompensationAmount.IsSet);
    }

    [Fact]
    public void Writing_An_Unset_Optional_Directly_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => JsonSerializer.Serialize(default(Optional<int?>), Web));
    }
}
