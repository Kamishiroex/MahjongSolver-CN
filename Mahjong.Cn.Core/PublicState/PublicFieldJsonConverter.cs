using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mahjong.Cn.PublicState;

/// <summary>Preserves absent values separately from explicitly observed false/zero/empty.</summary>
public sealed class PublicFieldJsonConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsGenericType
        && typeToConvert.GetGenericTypeDefinition() == typeof(Field<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(FieldConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;

    private sealed class FieldConverter<T> : JsonConverter<Field<T>>
    {
        public FieldConverter() { }
        public override Field<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException("A public field must be an object.");
            bool TryGet(string name, out JsonElement result)
            {
                string property = options.PropertyNamingPolicy?.ConvertName(name) ?? name;
                if (root.TryGetProperty(property, out result)) return true;
                if (options.PropertyNameCaseInsensitive)
                    foreach (var item in root.EnumerateObject())
                        if (string.Equals(item.Name, property, StringComparison.OrdinalIgnoreCase))
                        { result = item.Value; return true; }
                return false;
            }
            var field = new Field<T>
            {
                Availability = TryGet(nameof(Field<T>.Availability), out var availability) ? availability.Deserialize<Availability>(options) : Availability.Unknown,
                SourceKind = TryGet(nameof(Field<T>.SourceKind), out var source) ? source.Deserialize<SourceKind>(options) : SourceKind.LegacyAssumption,
                MappingStatus = TryGet(nameof(Field<T>.MappingStatus), out var mapping) ? mapping.Deserialize<MappingStatus>(options) : MappingStatus.Candidate,
                Observation = TryGet(nameof(Field<T>.Observation), out var observation) ? observation.Deserialize<ObservationReference>(options) : null,
                Reason = TryGet(nameof(Field<T>.Reason), out var reason) ? reason.GetString() : null,
            };
            return TryGet(nameof(Field<T>.Value), out var value) ? field with { Value = value.Deserialize<T>(options) } : field;
        }

        public override void Write(Utf8JsonWriter writer, Field<T> value, JsonSerializerOptions options)
        {
            void Property<TValue>(string name, TValue propertyValue)
            {
                writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName(name) ?? name);
                JsonSerializer.Serialize(writer, propertyValue, options);
            }
            writer.WriteStartObject();
            Property(nameof(value.Availability), value.Availability);
            Property(nameof(value.SourceKind), value.SourceKind);
            Property(nameof(value.MappingStatus), value.MappingStatus);
            if (value.HasValue) Property(nameof(value.Value), value.Value);
            if (value.Observation is not null) Property(nameof(value.Observation), value.Observation);
            if (value.Reason is not null) Property(nameof(value.Reason), value.Reason);
            writer.WriteEndObject();
        }
    }
}
