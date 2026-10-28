using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentCat.Internal.Json;

// Copies a JsonNode tree with an explicit stack, so trees built in code deeper than the call stack allows copy
// too. The walk pauses at each string leaf, in document order, so a synchronous or asynchronous caller can
// replace it. A container copy is attached to its parent copy only once it is complete: attaching walks the
// new parent's ancestors, which stays O(1) while that parent is still detached. Every container copy gets
// case-sensitive options, as JS objects are, whatever options the source carries. Numbers and booleans are
// copied by value, so numbers built in code (NaN and the infinities among them) survive the copy.
internal sealed class JsonTreeCopy
{
    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = JsJson.MaxDepth };

    private readonly Stack<Frame> _frames = new();
    private string? _rootText;
    private bool _pending;
    private string? _replacement;

    internal JsonTreeCopy(JsonNode? root)
    {
        if (root is null)
        {
            return;
        }

        var node = Expand(root);
        if (node is JsonObject or JsonArray)
        {
            _frames.Push(new Frame(node));
        }
        else if (node.GetValueKind() == JsonValueKind.String)
        {
            _rootText = StringOf((JsonValue)node);
        }
        else
        {
            Result = CopyScalar((JsonValue)node);
        }
    }

    internal string Current { get; private set; } = string.Empty;

    internal JsonNode? Result { get; private set; }

    internal static JsonNode? Copy(JsonNode? root, Func<string, string>? mapString = null)
    {
        var copy = new JsonTreeCopy(root);
        while (copy.MoveNext())
        {
            if (mapString is not null)
            {
                copy.Replace(mapString(copy.Current));
            }
        }

        return copy.Result;
    }

    internal static JsonNode Expand(JsonNode node)
    {
        if (node is JsonObject or JsonArray || node.GetValueKind() is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            return node;
        }

        return JsonNode.Parse(node.ToJsonString(), new JsonNodeOptions(), ParseOptions)!;
    }

    internal static string StringOf(JsonValue value)
    {
        if (value.TryGetValue(out JsonElement element) && element.ValueKind == JsonValueKind.String)
        {
            return JsJson.ReadString(element);
        }

        if (value.TryGetValue(out string? text))
        {
            return text;
        }

        using var document = JsonDocument.Parse(value.ToJsonString());
        return JsJson.ReadString(document.RootElement);
    }

    internal static JsonNode? CopyScalar(JsonValue value) => value.GetValueKind() switch
    {
        JsonValueKind.String => JsonValue.Create(StringOf(value)),
        JsonValueKind.True => JsonValue.Create(true),
        JsonValueKind.False => JsonValue.Create(false),
        JsonValueKind.Number => CopyNumber(value),
        _ => null,
    };

    // Advances to the next string leaf (false once the copy is complete and Result is set). The copy keeps
    // Current unless Replace is called before the next advance; a null replacement becomes a JSON null.
    internal bool MoveNext()
    {
        if (_pending)
        {
            _pending = false;
            Attach(JsonValue.Create(_replacement));
        }

        if (_rootText is not null)
        {
            Pause(_rootText);
            _rootText = null;
            return true;
        }

        while (_frames.Count > 0)
        {
            var frame = _frames.Peek();
            if (!frame.MoveNext())
            {
                _frames.Pop();
                Attach(frame.Copy);
                continue;
            }

            var child = frame.Current;
            if (child is null)
            {
                frame.Add(null);
                continue;
            }

            child = Expand(child);
            if (child is JsonObject or JsonArray)
            {
                _frames.Push(new Frame(child));
            }
            else if (child.GetValueKind() == JsonValueKind.String)
            {
                Pause(StringOf((JsonValue)child));
                return true;
            }
            else
            {
                frame.Add(CopyScalar((JsonValue)child));
            }
        }

        return false;
    }

    internal void Replace(string? text) => _replacement = text;

    private static JsonValue CopyNumber(JsonValue value)
    {
        if (value.TryGetValue(out JsonElement element))
        {
            return JsonValue.Create(element)!;
        }

        if (value.TryGetValue(out int i))
        {
            return JsonValue.Create(i);
        }

        if (value.TryGetValue(out long l))
        {
            return JsonValue.Create(l);
        }

        if (value.TryGetValue(out double d))
        {
            return JsonValue.Create(d);
        }

        if (value.TryGetValue(out float f))
        {
            return JsonValue.Create(f);
        }

        if (value.TryGetValue(out decimal m))
        {
            return JsonValue.Create(m);
        }

        if (value.TryGetValue(out short s))
        {
            return JsonValue.Create(s);
        }

        if (value.TryGetValue(out byte b))
        {
            return JsonValue.Create(b);
        }

        if (value.TryGetValue(out sbyte sb))
        {
            return JsonValue.Create(sb);
        }

        if (value.TryGetValue(out ushort us))
        {
            return JsonValue.Create(us);
        }

        if (value.TryGetValue(out uint ui))
        {
            return JsonValue.Create(ui);
        }

        if (value.TryGetValue(out ulong ul))
        {
            return JsonValue.Create(ul);
        }

        return (JsonValue)JsonNode.Parse(value.ToJsonString())!;
    }

    private void Pause(string text)
    {
        Current = text;
        _replacement = text;
        _pending = true;
    }

    private void Attach(JsonNode? node)
    {
        if (_frames.Count == 0)
        {
            Result = node;
        }
        else
        {
            _frames.Peek().Add(node);
        }
    }

    private sealed class Frame
    {
        private readonly KeyValuePair<string, JsonNode?>[]? _properties;
        private readonly JsonArray? _items;
        private int _next;
        private string _key = string.Empty;

        internal Frame(JsonNode source)
        {
            if (source is JsonObject obj)
            {
                _properties = PropertiesOf(obj);
                Copy = new JsonObject(new JsonNodeOptions());
            }
            else
            {
                _items = (JsonArray)source;
                Copy = new JsonArray(new JsonNodeOptions());
            }
        }

        internal JsonNode Copy { get; }

        private static KeyValuePair<string, JsonNode?>[] PropertiesOf(JsonObject obj)
        {
            try
            {
                return obj.ToArray();
            }
            catch (ArgumentException)
            {
                return ((JsonObject)JsonNode.Parse(obj.ToJsonString(), new JsonNodeOptions(), ParseOptions)!).ToArray();
            }
        }

        internal JsonNode? Current { get; private set; }

        internal bool MoveNext()
        {
            if (_properties is not null)
            {
                if (_next == _properties.Length)
                {
                    return false;
                }

                _key = _properties[_next].Key;
                Current = _properties[_next].Value;
                _next++;
                return true;
            }

            if (_next == _items!.Count)
            {
                return false;
            }

            Current = _items[_next];
            _next++;
            return true;
        }

        internal void Add(JsonNode? node)
        {
            if (Copy is JsonObject obj)
            {
                obj.Add(_key, node);
            }
            else
            {
                ((JsonArray)Copy).Add(node);
            }
        }
    }
}
