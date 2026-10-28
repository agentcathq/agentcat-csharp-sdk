using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCat.Internal.Events;

namespace AgentCat.Tests.Events;

public class EventSanitizerTests
{
    private const string SessionId = "ses_123000000000000000000000000";

    private const string ImageMarker = "[image content redacted - not supported by AgentCat]";
    private const string AudioMarker = "[audio content redacted - not supported by AgentCat]";
    private const string BlobMarker = "[binary resource content redacted - not supported by AgentCat]";
    private const string DataMarker = "[binary data redacted - not supported by AgentCat]";

    private static AgentCatEvent Event(string? parameters = null, string? response = null)
        => new()
        {
            SessionId = SessionId,
            Timestamp = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Parameters = parameters is null ? null : JsonNode.Parse(parameters),
            Response = response is null ? null : JsonNode.Parse(response),
        };

    private static string Marker(string text) => "{\"type\":\"text\",\"text\":\"" + text + "\"}";

    private static string Base64(int length) => new string('A', length - 1) + "=";

    [Fact]
    public void Replaces_image_and_audio_but_keeps_text()
    {
        var e = Event(response: "{\"content\":[{\"type\":\"text\",\"text\":\"Hello world\"},{\"type\":\"image\",\"data\":\"abc\",\"mimeType\":\"image/png\"},{\"type\":\"audio\",\"data\":\"abc\",\"mimeType\":\"audio/wav\"}]}");

        EventSanitizer.Sanitize(e);

        Assert.Equal(
            "{\"content\":[{\"type\":\"text\",\"text\":\"Hello world\"}," + Marker(ImageMarker) + "," + Marker(AudioMarker) + "]}",
            e.Response!.ToJsonString());
    }

    [Fact]
    public void Text_only_content_is_unchanged()
    {
        const string json = "{\"content\":[{\"type\":\"text\",\"text\":\"a\"},{\"type\":\"text\",\"text\":\"b\"}],\"isError\":false}";
        var e = Event(response: json);

        EventSanitizer.Sanitize(e);

        Assert.Equal(json, e.Response!.ToJsonString());
    }

    [Theory]
    [InlineData("\"blob\":\"QUJD\"")]
    [InlineData("\"blob\":\"\"")]
    public void Redacts_embedded_resource_with_blob(string blob)
    {
        var e = Event(response: "{\"content\":[{\"type\":\"resource\",\"resource\":{\"uri\":\"file:///a\"," + blob + ",\"mimeType\":\"application/pdf\"}}]}");

        EventSanitizer.Sanitize(e);

        Assert.Equal("{\"content\":[" + Marker(BlobMarker) + "]}", e.Response!.ToJsonString());
    }

    [Fact]
    public void Passes_through_resources_without_blob_resource_links_and_missing_resource()
    {
        const string json = "{\"content\":[{\"type\":\"resource\",\"resource\":{\"uri\":\"file:///a\",\"text\":\"hi\"}},{\"type\":\"resource\"},{\"type\":\"resource_link\",\"uri\":\"file:///b\",\"name\":\"b\"}]}";
        var e = Event(response: json);

        EventSanitizer.Sanitize(e);

        Assert.Equal(json, e.Response!.ToJsonString());
    }

    [Fact]
    public void Redacts_unknown_content_types_with_the_type_name()
    {
        var e = Event(response: "{\"content\":[{\"type\":\"video\",\"data\":\"x\"}]}");

        EventSanitizer.Sanitize(e);

        Assert.Equal("text", e.Response!["content"]![0]!["type"]!.GetValue<string>());
        Assert.Equal(
            "[unsupported content type \"video\" redacted - not supported by AgentCat]",
            e.Response["content"]![0]!["text"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{\"data\":\"x\"}", "undefined")]
    [InlineData("{\"type\":null}", "null")]
    [InlineData("{\"type\":5}", "5")]
    [InlineData("{\"type\":true}", "true")]
    [InlineData("{\"type\":{}}", "[object Object]")]
    public void Blocks_without_a_string_type_render_like_js(string block, string rendered)
    {
        var e = Event(response: "{\"content\":[" + block + "]}");

        EventSanitizer.Sanitize(e);

        var text = e.Response!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Equal("[unsupported content type \"" + rendered + "\" redacted - not supported by AgentCat]", text);
    }

    [Fact]
    public void Null_and_scalar_content_entries_pass_through()
    {
        const string json = "{\"content\":[null,5,\"str\",true]}";
        var e = Event(response: json);

        EventSanitizer.Sanitize(e);

        Assert.Equal(json, e.Response!.ToJsonString());
    }

    [Fact]
    public void Redacts_large_base64_inside_structured_content()
    {
        var e = Event(response: "{\"content\":[],\"structuredContent\":{\"label\":\"x\",\"n\":42,\"nested\":{\"data\":\"" + Base64(12000) + "\"},\"list\":[\"" + Base64(12000) + "\",true]}}");

        EventSanitizer.Sanitize(e);

        Assert.Equal(
            "{\"content\":[],\"structuredContent\":{\"label\":\"x\",\"n\":42,\"nested\":{\"data\":\"" + DataMarker + "\"},\"list\":[\"" + DataMarker + "\",true]}}",
            e.Response!.ToJsonString());
    }

    [Fact]
    public void Response_without_content_and_other_keys_are_not_scanned()
    {
        var big = Base64(12000);
        var json = "{\"_meta\":{\"blob\":\"" + big + "\"},\"other\":\"" + big + "\"}";
        var e = Event(response: json);

        EventSanitizer.Sanitize(e);

        Assert.Equal(json, e.Response!.ToJsonString());
    }

    [Fact]
    public void Null_response_and_parameters_stay_null()
    {
        var e = Event();

        EventSanitizer.Sanitize(e);

        Assert.Null(e.Response);
        Assert.Null(e.Parameters);
    }

    [Fact]
    public void Response_that_is_an_array_stays_an_array()
    {
        var e = Event(response: "[{\"type\":\"image\"},1]");

        EventSanitizer.Sanitize(e);

        Assert.IsType<JsonArray>(e.Response);
        Assert.Equal("[{\"type\":\"image\"},1]", e.Response!.ToJsonString());
    }

    [Fact]
    public void Leaves_small_strings_and_large_non_base64_strings()
    {
        var prose = string.Concat(Enumerable.Repeat("Hello, world! This is NOT base64. ", 400));
        var json = "{\"small\":\"" + new string('A', 5000) + "\",\"prose\":\"" + prose + "\"}";
        var e = Event(parameters: json);

        EventSanitizer.Sanitize(e);

        Assert.Equal(json, e.Parameters!.ToJsonString());
    }

    [Fact]
    public void Redacts_deeply_nested_and_array_base64_and_keeps_siblings()
    {
        var big = Base64(12000);
        var e = Event(parameters: "{\"l1\":{\"l2\":{\"l3\":{\"data\":\"" + big + "\",\"n\":42,\"ok\":true,\"s\":\"short\"}}},\"arr\":[\"" + big + "\",1,[\"" + big + "\"]],\"label\":\"x\"}");

        EventSanitizer.Sanitize(e);

        Assert.Equal(
            "{\"l1\":{\"l2\":{\"l3\":{\"data\":\"" + DataMarker + "\",\"n\":42,\"ok\":true,\"s\":\"short\"}}},\"arr\":[\"" + DataMarker + "\",1,[\"" + DataMarker + "\"]],\"label\":\"x\"}",
            e.Parameters!.ToJsonString());
    }

    [Fact]
    public void Base64_boundary_is_10240_utf16_units()
    {
        var e = Event(parameters: "{\"at\":\"" + Base64(10240) + "\",\"below\":\"" + Base64(10239) + "\"}");

        EventSanitizer.Sanitize(e);

        Assert.Equal(DataMarker, e.Parameters!["at"]!.GetValue<string>());
        Assert.Equal(Base64(10239), e.Parameters["below"]!.GetValue<string>());
        Assert.Equal(10240, EventSanitizer.Base64ScanMinLength);
    }

    [Fact]
    public void A_string_parameter_at_the_root_is_scanned()
    {
        var e = new AgentCatEvent { SessionId = SessionId, Parameters = JsonValue.Create(Base64(10240)) };

        EventSanitizer.Sanitize(e);

        Assert.Equal(DataMarker, e.Parameters!.GetValue<string>());
    }

    [Fact]
    public void Mime_wrapped_base64_is_replaced_and_trailing_newline_after_padding_is_not()
    {
        var wrapped = string.Concat(Enumerable.Repeat(new string('A', 76) + "\r\n", 200));
        var e = Event(parameters: "{\"mime\":" + JsonSerializer.Serialize(wrapped) + ",\"padNl\":" + JsonSerializer.Serialize(Base64(10240) + "\n") + "}");

        EventSanitizer.Sanitize(e);

        Assert.Equal(DataMarker, e.Parameters!["mime"]!.GetValue<string>());
        Assert.Equal(Base64(10240) + "\n", e.Parameters["padNl"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("url", "ab-_")]
    [InlineData("data", "data:image/png;base64,AAAA")]
    [InlineData("space", "AAAA AAAA")]
    public void Keeps_base64url_data_uris_and_strings_with_spaces(string name, string unit)
    {
        var value = string.Concat(Enumerable.Repeat(unit, 12000 / unit.Length + 1));
        var e = Event(parameters: "{\"" + name + "\":\"" + value + "\"}");

        EventSanitizer.Sanitize(e);

        Assert.Equal(value, e.Parameters![name]!.GetValue<string>());
    }

    [Fact]
    public void Does_not_mutate_input_nodes_and_never_shares_nodes()
    {
        var response = JsonNode.Parse("{\"content\":[{\"type\":\"image\"},{\"type\":\"text\",\"text\":\"t\"}],\"structuredContent\":{\"d\":\"" + Base64(10240) + "\"}}")!;
        var parameters = JsonNode.Parse("{\"a\":{\"b\":[\"" + Base64(10240) + "\"]},\"c\":{\"d\":1}}")!;
        var responseBefore = response.ToJsonString();
        var parametersBefore = parameters.ToJsonString();
        var e = new AgentCatEvent { SessionId = SessionId, Response = response, Parameters = parameters };

        EventSanitizer.Sanitize(e);

        Assert.Equal(responseBefore, response.ToJsonString());
        Assert.Equal(parametersBefore, parameters.ToJsonString());
        Assert.NotSame(response, e.Response);
        Assert.NotSame(parameters["c"], e.Parameters!["c"]);
        Assert.NotSame(response["content"]![1], e.Response!["content"]![1]);
    }

    [Fact]
    public void Other_event_fields_are_untouched()
    {
        var big = Base64(12000);
        var e = Event();
        e.UserIntent = big;
        e.Error = JsonNode.Parse("{\"message\":\"" + big + "\"}");
        e.IdentifyActorData = JsonNode.Parse("{\"k\":\"" + big + "\"}")!.AsObject();

        EventSanitizer.Sanitize(e);

        Assert.Equal(big, e.UserIntent);
        Assert.Equal(big, e.Error!["message"]!.GetValue<string>());
        Assert.Equal(big, e.IdentifyActorData!["k"]!.GetValue<string>());
    }

    [Fact]
    public void Copies_are_case_sensitive_even_when_the_source_is_not()
    {
        var parameters = new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true })
        {
            ["o"] = new JsonObject { ["a"] = 1, ["A"] = 2 },
        };
        var e = new AgentCatEvent { SessionId = SessionId, Parameters = parameters };

        EventSanitizer.Sanitize(e);

        Assert.Equal("{\"o\":{\"a\":1,\"A\":2}}", e.Parameters!.ToJsonString());
    }

    [Fact]
    public void Code_built_deep_trees_do_not_overflow()
    {
        JsonNode inner = new JsonObject { ["v"] = Base64(10240) };
        for (var i = 0; i < 10000; i++)
        {
            inner = new JsonObject { ["n"] = inner };
        }

        var e = new AgentCatEvent { SessionId = SessionId, Parameters = inner };

        EventSanitizer.Sanitize(e);

        var node = e.Parameters!;
        for (var i = 0; i < 10000; i++)
        {
            node = node["n"]!;
        }

        Assert.Equal(DataMarker, node["v"]!.GetValue<string>());
    }
}
