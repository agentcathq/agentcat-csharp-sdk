using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCat.Internal.Json;
using AgentCat.Internal.Text;

namespace AgentCat.Internal.Events;

internal static class EventTruncator
{
    internal const int MaxDepth = 10;
    internal const int MaxBreadth = 100;
    internal const int MaxStringLength = 32768;
    internal const int MaxEventBytes = 102400;
    internal const int MaxUserIntentLength = 2048;
    internal const int MaxErrorMessageLength = 2048;
    internal const int MaxResourceNameLength = 256;
    internal const int MaxNameVersionLength = 256;
    internal const int MaxStackFrames = 50;
    internal const int MaxContentTextLength = 32768;
    internal const string Suffix = "...";

    // error → chained_errors → entry → frames → frame: the depth the backend needs to parse ErrorData.
    internal const int MinErrorDepth = 5;

    private const int LargestStringRounds = 10;
    private const int LargestStringMinLength = 100;
    private const int LargestStringSlackBytes = 200;
    private const int LargestStringMinReduction = 10;

    private const string BreadthKey = "...";
    private const string BreadthMarker = "[MaxProperties ~]";

    private enum Field
    {
        Id,
        SessionId,
        ProjectId,
        EventType,
        IpAddress,
        SdkLanguage,
        AgentCatVersion,
        ServerName,
        ServerVersion,
        ClientName,
        ClientVersion,
        IdentifyActorGivenId,
        IdentifyActorName,
        ResourceName,
        UserIntent,
        Tags,
        IdentifyActorData,
        Parameters,
        Response,
        Error,
        Properties,
    }

    internal static void Truncate(AgentCatEvent e)
    {
        var w = new AgentCatEvent();
        EventShape.CopyInto(e, w);

        w.UserIntent = Cut(w.UserIntent, MaxUserIntentLength);
        w.ResourceName = Cut(w.ResourceName, MaxResourceNameLength);
        w.ServerName = Cut(w.ServerName, MaxNameVersionLength);
        w.ServerVersion = Cut(w.ServerVersion, MaxNameVersionLength);
        w.ClientName = Cut(w.ClientName, MaxNameVersionLength);
        w.ClientVersion = Cut(w.ClientVersion, MaxNameVersionLength);

        var error = LimitError(w.Error);
        var response = LimitContentText(w.Response);

        w.Parameters = Normalize(w.Parameters);
        w.Response = Normalize(response);
        w.IdentifyActorData = (JsonObject?)Normalize(w.IdentifyActorData);
        w.Error = Normalize(error);

        FitToBudget(w);
        EventShape.CopyInto(w, e);
    }

    internal static long MeasureBytes(AgentCatEvent e) => new SizeLedger(e).Total;

    internal static JsonNode? Normalize(
        JsonNode? value,
        int depth = MaxDepth,
        int breadth = MaxBreadth,
        int maxStringLength = MaxStringLength)
    {
        if (value is null)
        {
            return null;
        }

        var root = JsonTreeCopy.Expand(value);
        if (root is JsonValue scalar)
        {
            return NormalizeScalar(scalar, maxStringLength);
        }

        if (depth <= 0)
        {
            return Placeholder(root);
        }

        // A finished copy is attached to its parent copy only when its frame completes: attaching a node
        // walks the new parent's ancestors, which is O(depth) once the parent is itself attached.
        var stack = new Stack<CopyFrame>();
        stack.Push(new CopyFrame(root, EmptyLike(root), depth - 1));
        while (true)
        {
            var frame = stack.Peek();
            var done = !frame.Source.MoveNext();
            if (!done && frame.Source.Index >= breadth)
            {
                frame.AddBreadthMarker();
                done = true;
            }

            if (done)
            {
                stack.Pop();
                if (stack.Count == 0)
                {
                    return frame.Copy;
                }

                stack.Peek().Add(frame.Copy);
                continue;
            }

            var child = frame.Source.Value;
            if (child is null)
            {
                frame.Add(null);
                continue;
            }

            child = JsonTreeCopy.Expand(child);
            if (child is JsonValue childScalar)
            {
                frame.Add(NormalizeScalar(childScalar, maxStringLength));
            }
            else if (frame.RemainingDepth <= 0)
            {
                frame.Add(Placeholder(child));
            }
            else
            {
                stack.Push(new CopyFrame(child, EmptyLike(child), frame.RemainingDepth - 1));
            }
        }
    }

    private static string? Cut(string? text, int maxLength)
        => text is null || text.Length <= maxLength ? text : Shorten(text, maxLength);

    private static string Shorten(string text, int length)
#if NET
        => string.Concat(text.AsSpan(0, length), Suffix);
#else
        => text.Substring(0, length) + Suffix;
#endif

    private static JsonNode? DeepCopy(JsonNode? node) => JsonTreeCopy.Copy(node);

    // TS `{ ...error }` turns an array error into an index-keyed object; arrays stay arrays here, as array
    // responses do in sanitization.
    private static JsonNode? LimitError(JsonNode? error)
    {
        if (error is null)
        {
            return null;
        }

        var node = JsonTreeCopy.Expand(error);
        if (node is not JsonObject obj)
        {
            return node;
        }

        string? message = null;
        JsonNode? frames = null;
        var replaceMessage = false;
        var replaceFrames = false;
        foreach (var property in obj)
        {
            if (property.Key == "message")
            {
                replaceMessage = TryCutMessage(property.Value, out message);
            }
            else if (property.Key == "frames")
            {
                replaceFrames = TryLimitFrames(property.Value, out frames);
            }
        }

        if (!replaceMessage && !replaceFrames)
        {
            return node;
        }

        var copy = new JsonObject(new JsonNodeOptions());
        foreach (var property in obj)
        {
            if (replaceMessage && property.Key == "message")
            {
                copy.Add(property.Key, JsonValue.Create(message));
            }
            else if (replaceFrames && property.Key == "frames")
            {
                copy.Add(property.Key, frames);
            }
            else
            {
                copy.Add(property.Key, DeepCopy(property.Value));
            }
        }

        return copy;
    }

    // TS truncateString(message): null passes, `.length <= max` passes, anything without `.slice` throws.
    private static bool TryCutMessage(JsonNode? message, out string? cut)
    {
        cut = null;
        if (message is null)
        {
            return false;
        }

        var node = JsonTreeCopy.Expand(message);
        if (node is JsonValue value)
        {
            switch (value.GetValueKind())
            {
                case JsonValueKind.Null:
                    return false;
                case JsonValueKind.String:
                    var text = JsonTreeCopy.StringOf(value);
                    if (text.Length <= MaxErrorMessageLength)
                    {
                        return false;
                    }

                    cut = Cut(text, MaxErrorMessageLength);
                    return true;
            }
        }
        else if (node is JsonArray items && items.Count <= MaxErrorMessageLength)
        {
            return false;
        }

        throw new InvalidOperationException("The error message is not a string.");
    }

    // TS truncateStackFrames(frames): falsy values pass, `.length <= 50` passes, otherwise the first 25 and
    // last 25 entries are spread into an array (a string spreads into its code points).
    private static bool TryLimitFrames(JsonNode? frames, out JsonNode? limited)
    {
        limited = null;
        if (frames is null)
        {
            return false;
        }

        const int half = MaxStackFrames / 2;
        var node = JsonTreeCopy.Expand(frames);
        if (node is JsonArray items)
        {
            if (items.Count <= MaxStackFrames)
            {
                return false;
            }

            var kept = new JsonArray(new JsonNodeOptions());
            for (var i = 0; i < half; i++)
            {
                kept.Add(DeepCopy(items[i]));
            }

            for (var i = items.Count - half; i < items.Count; i++)
            {
                kept.Add(DeepCopy(items[i]));
            }

            limited = kept;
            return true;
        }

        if (node is JsonValue value)
        {
            switch (value.GetValueKind())
            {
                case JsonValueKind.Null:
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Number when IsZeroOrNaN(value):
                    return false;
                case JsonValueKind.String:
                    var text = JsonTreeCopy.StringOf(value);
                    if (text.Length <= MaxStackFrames)
                    {
                        return false;
                    }

                    var spread = new JsonArray(new JsonNodeOptions());
                    AddCodePoints(spread, text.Substring(0, half));
                    AddCodePoints(spread, text.Substring(text.Length - half));
                    limited = spread;
                    return true;
            }
        }

        throw new InvalidOperationException("The error frames are neither an array nor a string.");
    }

    private static bool IsZeroOrNaN(JsonValue value)
    {
        if (value.TryGetValue(out double d))
        {
            return d == 0 || double.IsNaN(d);
        }

        if (value.TryGetValue(out float f))
        {
            return f == 0 || float.IsNaN(f);
        }

        return double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed == 0;
    }

    private static void AddCodePoints(JsonArray target, string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                target.Add((JsonNode)text.Substring(i, 2));
                i++;
            }
            else
            {
                target.Add((JsonNode)text.Substring(i, 1));
            }
        }
    }

    private static JsonNode? LimitContentText(JsonNode? response)
    {
        if (response is null)
        {
            return null;
        }

        var node = JsonTreeCopy.Expand(response);
        if (node is not JsonObject obj
            || !TryGetExact(obj, "content", out var content)
            || content is null
            || JsonTreeCopy.Expand(content) is not JsonArray blocks)
        {
            return node;
        }

        var cuts = new string?[blocks.Count];
        var any = false;
        for (var i = 0; i < blocks.Count; i++)
        {
            cuts[i] = CutTextBlock(blocks[i]);
            any |= cuts[i] is not null;
        }

        if (!any)
        {
            return node;
        }

        var limited = new JsonArray(new JsonNodeOptions());
        for (var i = 0; i < blocks.Count; i++)
        {
            limited.Add(cuts[i] is { } text ? WithText((JsonObject)JsonTreeCopy.Expand(blocks[i]!), text) : DeepCopy(blocks[i]));
        }

        var copy = new JsonObject(new JsonNodeOptions());
        foreach (var property in obj)
        {
            copy.Add(property.Key, property.Key == "content" ? limited : DeepCopy(property.Value));
        }

        return copy;
    }

    private static string? CutTextBlock(JsonNode? block)
    {
        if (block is null
            || JsonTreeCopy.Expand(block) is not JsonObject obj
            || !TryGetExact(obj, "type", out var type)
            || !TryGetString(type, out var typeName)
            || typeName != "text"
            || !TryGetExact(obj, "text", out var textNode)
            || !TryGetString(textNode, out var text)
            || text.Length <= MaxContentTextLength)
        {
            return null;
        }

        return Cut(text, MaxContentTextLength);
    }

    private static JsonObject WithText(JsonObject block, string text)
    {
        var copy = new JsonObject(new JsonNodeOptions());
        foreach (var property in block)
        {
            copy.Add(property.Key, property.Key == "text" ? JsonValue.Create(text) : DeepCopy(property.Value));
        }

        return copy;
    }

    private static bool TryGetExact(JsonObject obj, string key, out JsonNode? value)
    {
        foreach (var property in obj)
        {
            if (property.Key == key)
            {
                value = property.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool TryGetString(JsonNode? node, out string text)
    {
        if (node is JsonValue value && value.GetValueKind() == JsonValueKind.String)
        {
            text = JsonTreeCopy.StringOf(value);
            return true;
        }

        text = "";
        return false;
    }

    private static void FitToBudget(AgentCatEvent w)
    {
        var size = new SizeLedger(w);
        if (size.Total <= MaxEventBytes)
        {
            return;
        }

        var identifyActorData = w.IdentifyActorData;
        var parameters = w.Parameters;
        var response = w.Response;
        var error = w.Error;

        // Unlike TS, Error keeps at least MinErrorDepth: below it frames collapse into placeholders and the
        // backend rejects the whole ErrorData. The largest-strings pass can still shorten its strings.
        for (var depth = MaxDepth - 1; depth >= 1; depth--)
        {
            w.IdentifyActorData = (JsonObject?)Normalize(identifyActorData, depth);
            w.Parameters = Normalize(parameters, depth);
            w.Response = Normalize(response, depth);
            w.Error = Normalize(error, Math.Max(depth, MinErrorDepth));
            size.Measure(w, Field.IdentifyActorData);
            size.Measure(w, Field.Parameters);
            size.Measure(w, Field.Response);
            size.Measure(w, Field.Error);
            if (size.Total <= MaxEventBytes)
            {
                return;
            }
        }

        TruncateLargestStrings(w, size);
        if (size.Total <= MaxEventBytes)
        {
            return;
        }

        w.Error = Normalize(error, 1);
        size.Measure(w, Field.Error);
        TruncateLargestStrings(w, size);
    }

    // TS truncateLargestFields on the depth-1 event (Error at MinErrorDepth). Tags and Properties may be shared with a customer hook's
    // output, so cuts go into copies that replace them only when one of their strings was shortened.
    private static void TruncateLargestStrings(AgentCatEvent w, SizeLedger size)
    {
        var tags = w.Tags;
        var properties = w.Properties;
        w.Tags = CopyTags(tags);
        w.Properties = (JsonObject?)DeepCopy(properties);
        var tagsCut = false;
        var propertiesCut = false;

        for (var round = 0; round < LargestStringRounds; round++)
        {
            var total = size.Total;
            if (total <= MaxEventBytes)
            {
                break;
            }

            var candidates = Collect(w);
            if (candidates.Count == 0)
            {
                break;
            }

            candidates.Sort(static (a, b) => a.Value.Length != b.Value.Length
                ? b.Value.Length.CompareTo(a.Value.Length)
                : a.Order.CompareTo(b.Order));

            var remaining = total - MaxEventBytes + LargestStringSlackBytes;
            var changed = new HashSet<Field>();
            foreach (var candidate in candidates)
            {
                if (remaining <= 0)
                {
                    break;
                }

                var length = candidate.Value.Length;
                var reduction = (int)Math.Min(remaining, length / 2);
                if (reduction < LargestStringMinReduction)
                {
                    continue;
                }

                Replace(w, candidate, Shorten(candidate.Value, length - reduction));
                changed.Add(candidate.Field);
                remaining -= reduction;
            }

            if (changed.Count == 0)
            {
                break;
            }

            tagsCut |= changed.Contains(Field.Tags);
            propertiesCut |= changed.Contains(Field.Properties);
            foreach (var field in changed)
            {
                size.Measure(w, field);
            }
        }

        if (!tagsCut)
        {
            w.Tags = tags;
        }

        if (!propertiesCut)
        {
            w.Properties = properties;
        }
    }

    private static Dictionary<string, string>? CopyTags(IDictionary<string, string>? tags)
    {
        if (tags is null)
        {
            return null;
        }

        var copy = new Dictionary<string, string>(tags.Count);
        foreach (var tag in tags)
        {
            copy.Add(tag.Key, tag.Value);
        }

        return copy;
    }

    private static List<Candidate> Collect(AgentCatEvent w)
    {
        var found = new List<Candidate>();
        AddScalar(found, Field.Id, w.Id);
        AddScalar(found, Field.SessionId, w.SessionId);
        AddScalar(found, Field.ProjectId, w.ProjectId);
        AddScalar(found, Field.EventType, w.EventType);
        AddScalar(found, Field.IpAddress, w.IpAddress);
        AddScalar(found, Field.SdkLanguage, w.SdkLanguage);
        AddScalar(found, Field.AgentCatVersion, w.AgentCatVersion);
        AddScalar(found, Field.ServerName, w.ServerName);
        AddScalar(found, Field.ServerVersion, w.ServerVersion);
        AddScalar(found, Field.ClientName, w.ClientName);
        AddScalar(found, Field.ClientVersion, w.ClientVersion);
        AddScalar(found, Field.IdentifyActorGivenId, w.IdentifyActorGivenId);
        AddScalar(found, Field.IdentifyActorName, w.IdentifyActorName);
        AddTree(found, Field.IdentifyActorData, w.IdentifyActorData);
        AddScalar(found, Field.ResourceName, w.ResourceName);
        AddTree(found, Field.Parameters, w.Parameters);
        AddTree(found, Field.Response, w.Response);
        AddScalar(found, Field.UserIntent, w.UserIntent);
        AddTree(found, Field.Error, w.Error);
        if (w.Tags is not null)
        {
            foreach (var tag in w.Tags)
            {
                if (tag.Value is not null && tag.Value.Length > LargestStringMinLength)
                {
                    found.Add(new Candidate(Field.Tags, null, tag.Key, -1, tag.Value, found.Count));
                }
            }
        }

        AddTree(found, Field.Properties, w.Properties);
        return found;
    }

    private static void AddScalar(List<Candidate> found, Field field, string? value)
    {
        if (value is not null && value.Length > LargestStringMinLength)
        {
            found.Add(new Candidate(field, null, null, -1, value, found.Count));
        }
    }

    private static void AddTree(List<Candidate> found, Field field, JsonNode? root)
    {
        if (root is null)
        {
            return;
        }

        if (root is JsonValue rootValue)
        {
            AddValue(found, field, null, null, -1, rootValue);
            return;
        }

        var stack = new Stack<Cursor>();
        stack.Push(new Cursor(root));
        while (stack.Count > 0)
        {
            var cursor = stack.Peek();
            if (!cursor.MoveNext())
            {
                stack.Pop();
                continue;
            }

            var child = cursor.Value;
            if (child is JsonValue value)
            {
                AddValue(found, field, cursor.Container, cursor.Key, cursor.Index, value);
            }
            else if (child is not null)
            {
                stack.Push(new Cursor(child));
            }
        }
    }

    private static void AddValue(List<Candidate> found, Field field, JsonNode? container, string? key, int index, JsonValue value)
    {
        if (value.GetValueKind() == JsonValueKind.String)
        {
            var text = JsonTreeCopy.StringOf(value);
            if (text.Length > LargestStringMinLength)
            {
                found.Add(new Candidate(field, container, key, index, text, found.Count));
            }
        }
    }

    private static void Replace(AgentCatEvent w, Candidate candidate, string text)
    {
        switch (candidate.Container)
        {
            case JsonObject obj:
                obj[candidate.Key!] = JsonValue.Create(text);
                return;
            case JsonArray array:
                array[candidate.Index] = JsonValue.Create(text);
                return;
        }

        switch (candidate.Field)
        {
            case Field.Id: w.Id = text; break;
            case Field.SessionId: w.SessionId = text; break;
            case Field.ProjectId: w.ProjectId = text; break;
            case Field.EventType: w.EventType = text; break;
            case Field.IpAddress: w.IpAddress = text; break;
            case Field.SdkLanguage: w.SdkLanguage = text; break;
            case Field.AgentCatVersion: w.AgentCatVersion = text; break;
            case Field.ServerName: w.ServerName = text; break;
            case Field.ServerVersion: w.ServerVersion = text; break;
            case Field.ClientName: w.ClientName = text; break;
            case Field.ClientVersion: w.ClientVersion = text; break;
            case Field.IdentifyActorGivenId: w.IdentifyActorGivenId = text; break;
            case Field.IdentifyActorName: w.IdentifyActorName = text; break;
            case Field.ResourceName: w.ResourceName = text; break;
            case Field.UserIntent: w.UserIntent = text; break;
            case Field.Tags: w.Tags![candidate.Key!] = text; break;
            case Field.Parameters: w.Parameters = JsonValue.Create(text); break;
            case Field.Response: w.Response = JsonValue.Create(text); break;
            case Field.Error: w.Error = JsonValue.Create(text); break;
            default: throw new InvalidOperationException("A payload string has no container.");
        }
    }

    private static JsonNode EmptyLike(JsonNode container)
        => container is JsonObject ? new JsonObject(new JsonNodeOptions()) : new JsonArray(new JsonNodeOptions());

    private static JsonValue Placeholder(JsonNode container)
        => JsonValue.Create(container is JsonArray ? "[Array]" : "[Object]");

    private static JsonNode? NormalizeScalar(JsonValue value, int maxStringLength)
    {
        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                var text = JsonTreeCopy.StringOf(value);
                return JsonValue.Create(text.Length > maxStringLength ? Shorten(text, maxStringLength) : text);
            case JsonValueKind.Number when JsSemantics.NonFiniteName(value) is { } name:
                return JsonValue.Create("[" + name + "]");
            default:
                return JsonTreeCopy.CopyScalar(value);
        }
    }

    private sealed class Candidate
    {
        internal Candidate(Field field, JsonNode? container, string? key, int index, string value, int order)
        {
            Field = field;
            Container = container;
            Key = key;
            Index = index;
            Value = value;
            Order = order;
        }

        internal Field Field { get; }

        internal JsonNode? Container { get; }

        internal string? Key { get; }

        internal int Index { get; }

        internal string Value { get; }

        internal int Order { get; }
    }

    // Byte length of JSON.stringify(event) as the sum of its parts: the TS-shaped event without payload trees,
    // plus `,"key":value` for each payload tree, so a pass re-measures only the parts it changed.
    private sealed class SizeLedger
    {
        private long _scalars;
        private long _identifyActorData;
        private long _parameters;
        private long _response;
        private long _error;
        private long _properties;

        internal SizeLedger(AgentCatEvent e)
        {
            _scalars = ScalarBytes(e);
            Measure(e, Field.IdentifyActorData);
            Measure(e, Field.Parameters);
            Measure(e, Field.Response);
            Measure(e, Field.Error);
            Measure(e, Field.Properties);
        }

        internal long Total => _scalars + _identifyActorData + _parameters + _response + _error + _properties;

        internal void Measure(AgentCatEvent e, Field field)
        {
            switch (field)
            {
                case Field.IdentifyActorData:
                    _identifyActorData = PartBytes("identifyActorData", e.IdentifyActorData);
                    break;
                case Field.Parameters:
                    _parameters = PartBytes("parameters", e.Parameters);
                    break;
                case Field.Response:
                    _response = PartBytes("response", e.Response);
                    break;
                case Field.Error:
                    _error = PartBytes("error", e.Error);
                    break;
                case Field.Properties:
                    _properties = PartBytes("properties", e.Properties);
                    break;
                default:
                    _scalars = ScalarBytes(e);
                    break;
            }
        }

        private static long ScalarBytes(AgentCatEvent e)
        {
            var scalars = new AgentCatEvent();
            EventShape.CopyInto(e, scalars);
            scalars.IdentifyActorData = null;
            scalars.Parameters = null;
            scalars.Response = null;
            scalars.Error = null;
            scalars.Properties = null;
            return JsJson.Length(EventShape.ToTsJson(scalars));
        }

        private static long PartBytes(string key, JsonNode? node)
            => node is null ? 0 : 1 + key.Length + 3 + JsJson.Length(node);
    }

    private sealed class Cursor
    {
        private readonly IEnumerator<KeyValuePair<string, JsonNode?>>? _properties;
        private readonly JsonArray? _items;

        internal Cursor(JsonNode container)
        {
            Container = container;
            if (container is JsonObject obj)
            {
                _properties = obj.GetEnumerator();
            }
            else
            {
                _items = (JsonArray)container;
            }
        }

        internal JsonNode Container { get; }

        internal string? Key { get; private set; }

        internal int Index { get; private set; } = -1;

        internal JsonNode? Value { get; private set; }

        internal bool MoveNext()
        {
            Index++;
            if (_properties is not null)
            {
                if (!_properties.MoveNext())
                {
                    return false;
                }

                Key = _properties.Current.Key;
                Value = _properties.Current.Value;
                return true;
            }

            if (Index >= _items!.Count)
            {
                return false;
            }

            Value = _items[Index];
            return true;
        }
    }

    private sealed class CopyFrame
    {
        internal CopyFrame(JsonNode source, JsonNode copy, int remainingDepth)
        {
            Source = new Cursor(source);
            Copy = copy;
            RemainingDepth = remainingDepth;
        }

        internal Cursor Source { get; }

        internal JsonNode Copy { get; }

        internal int RemainingDepth { get; }

        internal void Add(JsonNode? value)
        {
            if (Copy is JsonObject obj)
            {
                obj.Add(Source.Key!, value);
            }
            else
            {
                ((JsonArray)Copy).Add(value);
            }
        }

        internal void AddBreadthMarker()
        {
            if (Copy is JsonObject obj)
            {
                obj[BreadthKey] = BreadthMarker;
            }
            else
            {
                ((JsonArray)Copy).Add((JsonNode)BreadthMarker);
            }
        }
    }
}
