using AgentCat.Internal.Events;
using AgentCat.Internal.Logging;
using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.Events;

public class TagValidatorTests
{
    private static KeyValuePair<string, string> Tag(string key, string value) => new(key, value);

    private static KeyValuePair<string, string>[] Tags(params KeyValuePair<string, string>[] tags) => tags;

    private static string[] Strings(params string[] values) => values;

    private static string Repeat(char c, int count) => new(c, count);

    [Fact]
    public void A_dropped_tag_line_is_a_warning_for_the_host_logger_whatever_the_key_is_called()
    {
        var log = new RecordingLog();

        TagValidator.Validate(new[] { new KeyValuePair<string, string>("error key!", "v") }, log);

        var (line, severity) = Assert.Single(log.Written);
        Assert.StartsWith("Dropping invalid tag: \"error key!\"", line, StringComparison.Ordinal);
        Assert.Equal(LogSeverity.Warning, severity);
    }

    [Fact]
    public void Should_pass_through_valid_tags_unchanged()
    {
        var log = new RecordingLog();
        var result = TagValidator.Validate(
            Tags(Tag("env", "production"), Tag("trace_id", "abc-123"), Tag("region", "us-east-1")),
            log);

        Assert.NotNull(result);
        Assert.Equal(Strings("env", "trace_id", "region"), result!.Keys.ToArray());
        Assert.Equal(Strings("production", "abc-123", "us-east-1"), result.Values.ToArray());
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void Should_return_null_for_empty_object()
    {
        Assert.Null(TagValidator.Validate(Array.Empty<KeyValuePair<string, string>>(), new RecordingLog()));
    }

    [Fact]
    public void Null_tags_return_null()
    {
        Assert.Null(TagValidator.Validate(null, new RecordingLog()));
    }

    [Fact]
    public void Should_drop_keys_with_invalid_characters()
    {
        var log = new RecordingLog();
        var result = TagValidator.Validate(
            Tags(Tag("valid_key", "value"), Tag("invalid!key", "value"), Tag("good.key", "value")),
            log);

        Assert.Equal(Strings("valid_key", "good.key"), result!.Keys.ToArray());
        Assert.Equal(
            Strings("Dropping invalid tag: \"invalid!key\" — key contains invalid characters or is empty"),
            log.Lines);
    }

    [Fact]
    public void Should_drop_keys_longer_than_32_characters()
    {
        var log = new RecordingLog();
        var longKey = Repeat('a', 33);
        var result = TagValidator.Validate(Tags(Tag(longKey, "value"), Tag("short", "value")), log);

        Assert.Equal(Strings("short"), result!.Keys.ToArray());
        Assert.Equal(
            Strings("Dropping invalid tag: \"" + longKey + "\" — key exceeds max length of 32"),
            log.Lines);
    }

    [Fact]
    public void Key_of_exactly_32_characters_is_kept()
    {
        var key = Repeat('a', 32);
        var result = TagValidator.Validate(Tags(Tag(key, "v")), new RecordingLog());
        Assert.Equal(Strings(key), result!.Keys.ToArray());
    }

    [Fact]
    public void Should_drop_values_longer_than_200_characters()
    {
        var log = new RecordingLog();
        var result = TagValidator.Validate(Tags(Tag("key1", Repeat('a', 201)), Tag("key2", "short")), log);

        Assert.Equal(Strings("key2"), result!.Keys.ToArray());
        Assert.Equal(
            Strings("Dropping invalid tag: \"key1\" — value exceeds max length of 200"),
            log.Lines);
    }

    [Fact]
    public void Value_of_exactly_200_characters_is_kept()
    {
        var result = TagValidator.Validate(Tags(Tag("k", Repeat('a', 200))), new RecordingLog());
        Assert.NotNull(result);
    }

    [Fact]
    public void Should_drop_values_containing_newlines()
    {
        var log = new RecordingLog();
        var result = TagValidator.Validate(Tags(Tag("key1", "has\nnewline"), Tag("key2", "clean")), log);

        Assert.Equal(Strings("key2"), result!.Keys.ToArray());
        Assert.Equal(
            Strings("Dropping invalid tag: \"key1\" — value contains newline character"),
            log.Lines);
    }

    [Fact]
    public void Should_drop_non_string_values_at_runtime()
    {
        var log = new RecordingLog();
        var result = TagValidator.Validate(Tags(Tag("key1", null!), Tag("key2", "valid")), log);

        Assert.Equal(Strings("key2"), result!.Keys.ToArray());
        Assert.Equal(
            Strings("Dropping invalid tag: \"key1\" — non-string value (got object)"),
            log.Lines);
    }

    [Fact]
    public void Null_key_is_dropped_as_an_invalid_key()
    {
        var log = new RecordingLog();
        var result = TagValidator.Validate(Tags(Tag(null!, "v"), Tag("valid", "value")), log);

        Assert.Equal(Strings("valid"), result!.Keys.ToArray());
        Assert.Equal(
            Strings("Dropping invalid tag: \"\" — key contains invalid characters or is empty"),
            log.Lines);
    }

    [Fact]
    public void Accepts_a_read_only_dictionary_hook_result()
    {
        IReadOnlyDictionary<string, string> hookResult = new Dictionary<string, string>
        {
            ["env"] = "production",
            ["bad!"] = "x",
        };
        var log = new RecordingLog();

        var result = TagValidator.Validate(hookResult, log);

        Assert.Equal(Strings("env"), result!.Keys.ToArray());
        Assert.Equal(
            Strings("Dropping invalid tag: \"bad!\" — key contains invalid characters or is empty"),
            log.Lines);
    }

    [Fact]
    public void Should_drop_empty_string_keys()
    {
        var log = new RecordingLog();
        var result = TagValidator.Validate(Tags(Tag(string.Empty, "empty-key-value"), Tag("valid", "value")), log);

        Assert.Equal(Strings("valid"), result!.Keys.ToArray());
        Assert.Equal(
            Strings("Dropping invalid tag: \"\" — key contains invalid characters or is empty"),
            log.Lines);
    }

    [Fact]
    public void Should_keep_only_first_50_entries_when_exceeding_limit()
    {
        var log = new RecordingLog();
        var tags = new List<KeyValuePair<string, string>>();
        for (var i = 0; i < 60; i++)
        {
            tags.Add(Tag("key" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture), "value" + i));
        }

        var result = TagValidator.Validate(tags, log);

        Assert.Equal(50, result!.Count);
        Assert.Equal("key000", result.Keys.First());
        Assert.Equal("key049", result.Keys.Last());
        Assert.Equal(Strings("Dropping 10 tag(s) — exceeds maximum of 50 entries per event"), log.Lines);
    }

    [Fact]
    public void Should_return_null_when_all_entries_are_invalid()
    {
        var log = new RecordingLog();
        var result = TagValidator.Validate(Tags(Tag("!!!", "value"), Tag("###", "value")), log);

        Assert.Null(result);
        Assert.Equal(2, log.Lines.Count);
    }

    [Fact]
    public void Should_allow_keys_with_periods_colons_dashes_spaces_and_dollar_signs()
    {
        var keys = Strings("my.tag", "my:tag", "my-tag", "my tag", "$ai_trace_id", "my$key");
        var result = TagValidator.Validate(keys.Select(k => Tag(k, "value")), new RecordingLog());

        Assert.Equal(keys, result!.Keys.ToArray());
    }

    [Fact]
    public void Key_with_trailing_newline_is_rejected()
    {
        var log = new RecordingLog();
        var result = TagValidator.Validate(Tags(Tag("key\n", "v")), log);

        Assert.Null(result);
        Assert.Single(log.Lines);
    }

    [Fact]
    public void Value_with_carriage_return_only_is_kept()
    {
        var result = TagValidator.Validate(Tags(Tag("k", "a\rb")), new RecordingLog());
        Assert.Equal("a\rb", result!["k"]);
    }

    [Fact]
    public void Null_log_instance_discards_messages()
    {
        NullAgentCatLog.Instance.Write("ignored");
        var result = TagValidator.Validate(Tags(Tag("!", "v")), NullAgentCatLog.Instance);
        Assert.Null(result);
    }
}
