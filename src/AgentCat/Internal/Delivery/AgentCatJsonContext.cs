using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AgentCat.Internal.Delivery;

[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(RawJson))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class AgentCatJsonContext : JsonSerializerContext;
