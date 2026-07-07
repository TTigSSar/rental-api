using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RentalPlatform.Api.Serialization;

/// <summary>
/// Serializes <see cref="DateTime"/> values as UTC ISO-8601 strings ending in "Z", regardless of
/// the value's <see cref="DateTimeKind"/>. This corrects a bug where EF Core reads back
/// <c>datetime2</c> columns (which have no timezone) as <see cref="DateTimeKind.Unspecified"/>,
/// causing System.Text.Json to omit the "Z" suffix and downstream clients (e.g. the Angular
/// frontend's <c>new Date(value)</c>) to misinterpret the value as local time.
///
/// This is a JSON-boundary-only fix: it does not change how timestamps are stored or the EF model.
/// All timestamps in this codebase are UTC by convention (<c>DateTime.UtcNow</c>), so Unspecified
/// values are treated as UTC rather than local time.
/// </summary>
public sealed class UtcDateTimeJsonConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        if (string.IsNullOrEmpty(value))
        {
            return default;
        }

        var parsed = DateTime.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

        return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utcValue = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        writer.WriteStringValue(utcValue.ToString("O", CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// Nullable counterpart of <see cref="UtcDateTimeJsonConverter"/>. See that type for the rationale.
/// </summary>
public sealed class UtcNullableDateTimeJsonConverter : JsonConverter<DateTime?>
{
    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        var value = reader.GetString();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var parsed = DateTime.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

        return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        var utcValue = value.Value.Kind switch
        {
            DateTimeKind.Utc => value.Value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
        };

        writer.WriteStringValue(utcValue.ToString("O", CultureInfo.InvariantCulture));
    }
}
