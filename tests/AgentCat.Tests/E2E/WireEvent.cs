using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.E2E;

// Typed reads over one event body as the AgentCat API received it (snake_case JSON). A member is null when the body
// lacks the key or holds null; Has tells the two apart.
internal sealed class WireEvent
{
    private WireEvent(JsonObject body, int arrival)
    {
        Body = body;
        Arrival = arrival;
    }

    internal JsonObject Body { get; }

    // The position at which the fake API received this body (0 first).
    internal int Arrival { get; }

    internal string? Id => String("id");

    internal string? ProjectId => String("project_id");

    internal string? EventType => String("event_type");

    internal string? SessionId => String("session_id");

    internal string? ResourceName => String("resource_name");

    internal string? UserIntent => String("user_intent");

    internal DateTimeOffset? Timestamp
        => String("timestamp") is { } text ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null;

    internal int? Duration => Body["duration"] is JsonValue value ? value.GetValue<int>() : null;

    internal JsonObject? Tags => Body["tags"] as JsonObject;

    internal JsonObject? Parameters => Body["parameters"] as JsonObject;

    // parameters.request.params.arguments: the arguments as the client sent them, injected ones included.
    internal JsonObject? Arguments => Parameters?["request"]?["params"]?["arguments"] as JsonObject;

    internal JsonObject? Extra => Parameters?["extra"] as JsonObject;

    // parameters.extra.requestId when it is a number: it grows with every request of one client.
    internal long? RequestId => Extra?["requestId"] is JsonValue value && value.GetValueKind() == JsonValueKind.Number ? value.GetValue<long>() : null;

    internal JsonNode? Response => Body["response"];

    internal bool? IsError => Body["is_error"] is JsonValue value ? value.GetValue<bool>() : null;

    internal JsonNode? Error => Body["error"];

    internal string? IdentifyActorGivenId => String("identify_actor_given_id");

    internal string? IdentifyActorName => String("identify_actor_name");

    internal JsonNode? IdentifyData => Body["identify_data"];

    internal string? ActorId => String("actor_id");

    internal JsonObject? Properties => Body["properties"] as JsonObject;

    internal string? ServerName => String("server_name");

    internal string? ServerVersion => String("server_version");

    internal string? ClientName => String("client_name");

    internal string? ClientVersion => String("client_version");

    internal static WireEvent Parse(RecordedRequest request, int arrival)
        => new(JsonNode.Parse(Encoding.UTF8.GetString(request.Body))!.AsObject(), arrival);

    internal bool Has(string key) => Body.ContainsKey(key);

    internal string? Tag(string key) => Tags?[key]?.GetValue<string>();

    internal string? Argument(string key) => Arguments?[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String
        ? value.GetValue<string>()
        : null;

    public override string ToString() => Body.ToJsonString();

    private string? String(string key) => Body[key] is JsonValue value ? value.GetValue<string>() : null;
}
