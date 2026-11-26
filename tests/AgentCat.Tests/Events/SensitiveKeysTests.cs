using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Events;
using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.Events;

// The shared vectors of the sensitive key mask (Fixtures/sensitive-keys/vectors.json, made from the reference that
// every AgentCat SDK ports), and the C# value kinds that the vectors cannot hold.
public class SensitiveKeysTests
{
    private static JsonNode Vectors => Fixtures.Json("sensitive-keys/vectors.json");

    [Fact]
    public void Every_key_vector_matches()
    {
        var failures = Vectors["keys"]!.AsArray()
            .Where(c => SensitiveKeys.IsSensitiveKey((string)c!["key"]!) != (bool)c!["sensitive"]!)
            .Select(c => c!.ToJsonString())
            .ToArray();

        Assert.Empty(failures);
        Assert.Equal(SensitiveKeys.Mask, (string?)Vectors["mask"]);
    }

    [Fact]
    public void Every_tree_vector_matches_and_leaves_its_input_unchanged()
    {
        foreach (var tree in Vectors["trees"]!.AsArray())
        {
            var input = tree!["input"];
            var before = input?.ToJsonString();

            var masked = SensitiveKeys.MaskSensitiveKeys(input);

            Assert.True(JsonNode.DeepEquals(tree["expected"], masked), (string)tree["name"]! + ": " + masked?.ToJsonString());
            Assert.Equal(before, input?.ToJsonString());
        }
    }

    // A JsonValue that holds an object is a map, as the sanitizer and the truncator read it. A map or list deeper than
    // the SDK can serialize becomes the mask. On a thread with a small stack (a secondary thread on macOS) the walk
    // stops earlier, and it still ends in the mask: it never throws and never overflows the stack.
    [Fact]
    public void Typed_values_are_maps_and_deep_trees_end_in_the_mask()
    {
        var headers = new Dictionary<string, List<string>> { ["authorization"] = ["Bearer t"], ["user-agent"] = ["a"] };
        var typed = new JsonObject { ["headers"] = JsonValue.Create(headers, RedactorTestJsonContext.Default.DictionaryStringListString) };
        Assert.Equal(
            """{"headers":{"authorization":"[redacted]","user-agent":["a"]}}""",
            SensitiveKeys.MaskSensitiveKeys(typed)!.ToJsonString());

        var deep = new JsonArray();
        var leaf = deep;
        for (var i = 0; i < 5000; i++)
        {
            var next = new JsonArray();
            leaf.Add(next);
            leaf = next;
        }

        AssertEndsInTheMask(SensitiveKeys.MaskSensitiveKeys(deep));

        JsonNode? onASmallStack = null;
        var thread = new Thread(() => onASmallStack = SensitiveKeys.MaskSensitiveKeys(deep), 256 * 1024);
        thread.Start();
        thread.Join();
        AssertEndsInTheMask(onASmallStack);
    }

    private static void AssertEndsInTheMask(JsonNode? node)
    {
        var depth = 0;
        while (node is JsonArray list)
        {
            node = list.Single();
            depth++;
        }

        Assert.Equal("[redacted]", (string?)node);
        Assert.InRange(depth, 1, 1000);
    }

    // A field that the mask cannot walk (here an object with a duplicate key) is dropped, with one log line, and the
    // event still goes out.
    [Fact]
    public async Task A_field_that_fails_is_dropped_and_the_event_still_goes_out()
    {
        var log = new RecordingLog();
        var sender = new RecordingSender();
        using var document = JsonDocument.Parse("""{"a":1,"a":2}""");
        var e = new AgentCatEvent
        {
            SessionId = "ses_1",
            ProjectId = "proj_1",
            EventType = "mcp:tools/call",
            Parameters = new JsonObject { ["name"] = "search" },
            Error = JsonObject.Create(document.RootElement),
        };

        await new EventProcessor(sender, log, new ManualClock()).ProcessAsync(new QueuedEvent(e), TestContext.Current.CancellationToken);

        var sent = JsonNode.Parse(Assert.Single(sender.SentJson))!.AsObject();
        Assert.Null(sent["error"]);
        Assert.Equal("search", (string?)sent["parameters"]!["name"]);
        Assert.Single(log.Lines, l => l.StartsWith("Failed to mask sensitive keys in ", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.StartsWith("Failed to mask sensitive keys in error: ", StringComparison.Ordinal));
    }
}
