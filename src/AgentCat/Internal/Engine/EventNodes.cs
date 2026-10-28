using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using AgentCat.Internal.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace AgentCat.Internal.Engine;

// Event trees are built in code: case-sensitive objects (JS objects are), duplicate keys resolved as JSON.parse
// does (the last value at the first position), keys and strings read leniently so lone surrogates survive, and
// numbers kept as their source text. The walk keeps its own stack, because the pipeline runs it on thread-pool
// threads whose stacks can be small. A container copy is attached to its parent copy only once it is complete,
// so attaching never walks a long chain of ancestors.
internal static class EventNodes
{
    private static readonly JsonTypeInfo ResultInfo = McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(CallToolResult));

    private static readonly JsonReaderOptions SourceOptions = new() { MaxDepth = JsJson.MaxDepth + 1 };

    internal static JsonNode? FromResult(CallToolResult result) => FromElement(JsonSerializer.SerializeToElement(result, ResultInfo));

    internal static JsonNode? FromElement(JsonElement element) => Build(Source.Of(element));

    internal static JsonNode? FromJson(JsonNode? node) => Build(Source.Of(node));

    private static JsonNode? Build(Source root)
    {
        var frames = new Stack<Frame>();
        if (!TryEnter(root, 0, null, frames, out var leaf))
        {
            return leaf;
        }

        while (true)
        {
            var frame = frames.Peek();
            if (frame.MoveNext(out var key, out var child))
            {
                if (!TryEnter(child, frame.Depth + 1, key, frames, out var value))
                {
                    frame.Attach(key, value);
                }

                continue;
            }

            frames.Pop();
            if (frames.Count == 0)
            {
                return frame.Copy;
            }

            frames.Peek().Attach(frame.Key, frame.Copy);
        }
    }

    // Pushes a frame for a container and returns true; returns false with the copy of anything else.
    private static bool TryEnter(Source source, int depth, string? key, Stack<Frame> frames, out JsonNode? leaf)
    {
        leaf = null;
        if (source.IsElement)
        {
            var element = source.Element;
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                case JsonValueKind.Array:
                    JsJson.CheckDepth(depth);
                    frames.Push(Frame.ForElement(element, depth, key));
                    return true;
                case JsonValueKind.String:
                    leaf = JsonValue.Create(JsJson.ReadString(element));
                    return false;
                case JsonValueKind.Number:
                    leaf = JsonValue.Create(element);
                    return false;
                case JsonValueKind.True:
                    leaf = JsonValue.Create(true);
                    return false;
                case JsonValueKind.False:
                    leaf = JsonValue.Create(false);
                    return false;
                case JsonValueKind.Null:
                    return false;
                default:
                    throw new ArgumentException("The element holds no JSON value.", nameof(source));
            }
        }

        switch (source.Node)
        {
            case null:
                return false;
            case JsonObject obj:
            {
                JsJson.CheckDepth(depth);
                KeyValuePair<string, JsonNode?>[] properties;
                try
                {
                    properties = obj.ToArray();
                }
                catch (ArgumentException)
                {
                    return TryEnter(Source.Of(Reparse(obj)), depth, key, frames, out leaf);
                }

                frames.Push(Frame.ForProperties(properties, depth, key));
                return true;
            }

            case JsonArray array:
                JsJson.CheckDepth(depth);
                frames.Push(Frame.ForItems(array, depth, key));
                return true;
            default:
            {
                var value = (JsonValue)source.Node;
                if (value.TryGetValue(out JsonElement element))
                {
                    return TryEnter(Source.Of(element), depth, key, frames, out leaf);
                }

                if (value.GetValueKind() is JsonValueKind.Object or JsonValueKind.Array)
                {
                    return TryEnter(Source.Of(Reparse(value)), depth, key, frames, out leaf);
                }

                leaf = JsonTreeCopy.CopyScalar(value);
                return false;
            }
        }
    }

    // A parsed object whose keys collide (duplicates, or case-insensitive options) throws when enumerated, while
    // its source still writes; so does a value that holds an object of another type.
    private static JsonElement Reparse(JsonNode node)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            node.WriteTo(writer);
        }

        var reader = new Utf8JsonReader(new ReadOnlySpan<byte>(buffer.GetBuffer(), 0, (int)buffer.Length), SourceOptions);
        return JsonElement.ParseValue(ref reader);
    }

    private readonly struct Source
    {
        private Source(JsonElement element, JsonNode? node, bool isElement)
        {
            Element = element;
            Node = node;
            IsElement = isElement;
        }

        internal JsonElement Element { get; }

        internal JsonNode? Node { get; }

        internal bool IsElement { get; }

        internal static Source Of(JsonElement element) => new(element, null, true);

        internal static Source Of(JsonNode? node) => new(default, node, false);
    }

    private sealed class Frame
    {
        private readonly bool _isObject;
        private readonly KeyValuePair<string, JsonNode?>[]? _properties;
        private readonly JsonArray? _items;
        private readonly bool _fromElement;
        private JsonElement.ObjectEnumerator _elementProperties;
        private JsonElement.ArrayEnumerator _elementItems;
        private int _next;

        private Frame(bool isObject, int depth, string? key)
        {
            _isObject = isObject;
            Depth = depth;
            Key = key;
            Copy = isObject ? new JsonObject(new JsonNodeOptions()) : new JsonArray(new JsonNodeOptions());
        }

        private Frame(JsonElement element, int depth, string? key)
            : this(element.ValueKind == JsonValueKind.Object, depth, key)
        {
            _fromElement = true;
            if (_isObject)
            {
                _elementProperties = element.EnumerateObject();
            }
            else
            {
                _elementItems = element.EnumerateArray();
            }
        }

        private Frame(KeyValuePair<string, JsonNode?>[] properties, int depth, string? key)
            : this(true, depth, key)
            => _properties = properties;

        private Frame(JsonArray items, int depth, string? key)
            : this(false, depth, key)
            => _items = items;

        internal int Depth { get; }

        internal string? Key { get; }

        internal JsonNode Copy { get; }

        internal static Frame ForElement(JsonElement element, int depth, string? key) => new(element, depth, key);

        internal static Frame ForProperties(KeyValuePair<string, JsonNode?>[] properties, int depth, string? key) => new(properties, depth, key);

        internal static Frame ForItems(JsonArray items, int depth, string? key) => new(items, depth, key);

        internal bool MoveNext(out string? key, out Source child)
        {
            key = null;
            child = default;
            if (_fromElement)
            {
                if (_isObject)
                {
                    if (!_elementProperties.MoveNext())
                    {
                        return false;
                    }

                    var property = _elementProperties.Current;
                    key = JsJson.ReadName(property);
                    child = Source.Of(property.Value);
                    return true;
                }

                if (!_elementItems.MoveNext())
                {
                    return false;
                }

                child = Source.Of(_elementItems.Current);
                return true;
            }

            if (_properties is not null)
            {
                if (_next == _properties.Length)
                {
                    return false;
                }

                key = _properties[_next].Key;
                child = Source.Of(_properties[_next].Value);
                _next++;
                return true;
            }

            if (_next == _items!.Count)
            {
                return false;
            }

            child = Source.Of(_items[_next]);
            _next++;
            return true;
        }

        internal void Attach(string? key, JsonNode? node)
        {
            if (_isObject)
            {
                ((JsonObject)Copy)[key!] = node;
            }
            else
            {
                ((JsonArray)Copy).Add(node);
            }
        }
    }
}
