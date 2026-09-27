using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mahjong.Cn.PublicState;

/// <summary>Legacy default immutable arrays mean absent, not an observed empty river/meld list.</summary>
public sealed class LegacySeatObservationJsonConverter : JsonConverter<LegacySeatObservation>
{
    public override LegacySeatObservation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        string Name(string name) => options.PropertyNamingPolicy?.ConvertName(name) ?? name;
        var root = document.RootElement;
        return new(root.GetProperty(Name(nameof(LegacySeatObservation.WindOrderIndex))).GetInt32(),
            root.GetProperty(Name(nameof(LegacySeatObservation.Seat))).Deserialize<VisibleSeat>(options)
                ?? throw new JsonException("Legacy seat is missing."),
            root.GetProperty(Name(nameof(LegacySeatObservation.Observation))).Deserialize<ObservationReference>(options)
                ?? throw new JsonException("Legacy seat source is missing."));
    }

    public override void Write(Utf8JsonWriter writer, LegacySeatObservation value, JsonSerializerOptions options)
    {
        string Name(string name) => options.PropertyNamingPolicy?.ConvertName(name) ?? name;
        void Property<T>(string name, T propertyValue)
        {
            writer.WritePropertyName(Name(name));
            JsonSerializer.Serialize(writer, propertyValue, options);
        }
        writer.WriteStartObject();
        Property(nameof(value.WindOrderIndex), value.WindOrderIndex);
        Property(nameof(value.Observation), value.Observation);
        Property(nameof(value.SourceKind), value.SourceKind);
        Property(nameof(value.MappingStatus), value.MappingStatus);
        writer.WritePropertyName(Name(nameof(value.Seat)));
        writer.WriteStartObject();
        var seat = value.Seat;
        if (seat.Wind.HasValue) Property(nameof(seat.Wind), seat.Wind.Value);
        if (seat.Score.HasValue) Property(nameof(seat.Score), seat.Score.Value);
        if (seat.Riichi.HasValue) Property(nameof(seat.Riichi), seat.Riichi.Value);
        if (seat.Ippatsu.HasValue) Property(nameof(seat.Ippatsu), seat.Ippatsu.Value);
        if (seat.RiichiDiscardIndex.HasValue) Property(nameof(seat.RiichiDiscardIndex), seat.RiichiDiscardIndex.Value);
        if (!seat.River.IsDefault) Property(nameof(seat.River), seat.River);
        if (!seat.Melds.IsDefault) Property(nameof(seat.Melds), seat.Melds);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}
