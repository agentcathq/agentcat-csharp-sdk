using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using AgentCat.Internal.Json;

namespace AgentCat.Internal.Telemetry;

/// <summary>
/// Writes an exporter's request body, in the order of the calls, with the bytes <c>JSON.stringify</c> writes (the
/// rules of <see cref="JsJson"/>). <see cref="Node"/> writes an event tree's bytes: the tree is never attached to
/// anything, so one tree can go into any number of payloads.
/// </summary>
internal sealed class PayloadWriter
{
    private readonly JsJson.Output _output = JsJson.Output.Writing();

    // One entry per open object or array: whether it has a member yet.
    private readonly List<bool> _hasMembers = new();

    // A property name was written and its value comes next.
    private bool _afterName;

    internal void StartObject()
    {
        BeforeValue();
        _output.Append((byte)'{');
        _hasMembers.Add(false);
    }

    internal void EndObject() => End((byte)'}');

    internal void StartArray()
    {
        BeforeValue();
        _output.Append((byte)'[');
        _hasMembers.Add(false);
    }

    internal void EndArray() => End((byte)']');

    internal void Property(string name)
    {
        Separate();
        JsJson.WriteString(_output, name);
        _output.Append((byte)':');
        _afterName = true;
    }

    /// <summary>Writes the string, or <c>null</c> for a null one.</summary>
    internal void String(string? value)
    {
        BeforeValue();
        if (value is null)
        {
            _output.Append("null");
        }
        else
        {
            JsJson.WriteString(_output, value);
        }
    }

    /// <summary>ECMAScript <c>Number::toString</c>; <c>null</c> for NaN and the infinities.</summary>
    internal void Number(double value)
    {
        BeforeValue();
        _output.Append(JsJson.NumberText(value));
    }

    internal void Number(long value)
    {
        BeforeValue();
        _output.Append(value.ToString(CultureInfo.InvariantCulture));
    }

    internal void Bool(bool value)
    {
        BeforeValue();
        _output.Append(value ? "true" : "false");
    }

    /// <summary>The string <c>Date.prototype.toISOString</c> gives: UTC, milliseconds, <c>Z</c>.</summary>
    internal void Date(DateTimeOffset value)
    {
        BeforeValue();
        JsJson.WriteString(_output, JsJson.DateText(value.UtcDateTime));
    }

    /// <summary>Writes the bytes of <paramref name="node"/> (<c>null</c> for a null node) without attaching it.</summary>
    internal void Node(JsonNode? node)
    {
        BeforeValue();
        JsJson.WriteNode(_output, node, _hasMembers.Count);
    }

    internal byte[] ToArray() => _output.ToArray();

    private void BeforeValue()
    {
        if (_afterName)
        {
            _afterName = false;
            return;
        }

        if (_hasMembers.Count > 0)
        {
            Separate();
        }
    }

    private void Separate()
    {
        var last = _hasMembers.Count - 1;
        if (_hasMembers[last])
        {
            _output.Append((byte)',');
        }

        _hasMembers[last] = true;
    }

    private void End(byte close)
    {
        _hasMembers.RemoveAt(_hasMembers.Count - 1);
        _output.Append(close);
    }
}
