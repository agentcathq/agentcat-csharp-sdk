using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentCat.Internal.Delivery;

[JsonConverter(typeof(RawJsonConverter))]
internal sealed class RawJson(byte[] utf8)
{
    internal byte[] Utf8 { get; } = utf8;
}

internal sealed class RawJsonConverter : JsonConverter<RawJson>
{
    public override RawJson Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, RawJson value, JsonSerializerOptions options)
        => writer.WriteRawValue(value.Utf8, skipInputValidation: true);
}
