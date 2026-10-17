using System.Text.Json;
using System.Text.Json.Serialization;

namespace RentalPlatform.Application.Common;

/// <summary>
/// Carries "was this property present in the request?" alongside its value, so a partial update can
/// tell an ABSENT property (<c>default</c>, <see cref="IsSet"/> = false: leave unchanged) from an
/// explicit JSON <c>null</c> (<see cref="IsSet"/> = true, <see cref="Value"/> = null: clear) and from
/// a value (set). Used by <c>UpdateListingRequest</c> (ADR-040). Use a nullable <typeparamref name="T"/>
/// (<c>Optional&lt;int?&gt;</c>, <c>Optional&lt;string?&gt;</c>) so an explicit null is representable.
/// The converter is attached here (System.Text.Json is BCL) so binding works with any serializer
/// options and Application stays free of web dependencies.
/// </summary>
[JsonConverter(typeof(OptionalJsonConverterFactory))]
public readonly struct Optional<T>
{
    public Optional(T value)
    {
        IsSet = true;
        Value = value;
    }

    public bool IsSet { get; }

    public T Value { get; }

    public static implicit operator Optional<T>(T value) => new(value);
}

public sealed class OptionalJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Optional<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var inner = typeToConvert.GetGenericArguments()[0];
        return (JsonConverter)Activator.CreateInstance(typeof(OptionalJsonConverter<>).MakeGenericType(inner))!;
    }

    private sealed class OptionalJsonConverter<T> : JsonConverter<Optional<T>>
    {
        // Value-type converters receive JSON null in Read (HandleNull defaults to true for them), which is
        // what makes an explicit null distinguishable from an absent property (never read at all).
        public override Optional<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return new Optional<T>(default!);
            }

            return new Optional<T>(JsonSerializer.Deserialize<T>(ref reader, options)!);
        }

        public override void Write(Utf8JsonWriter writer, Optional<T> value, JsonSerializerOptions options)
        {
            if (!value.IsSet)
            {
                writer.WriteNullValue();
                return;
            }

            JsonSerializer.Serialize(writer, value.Value, options);
        }
    }
}
