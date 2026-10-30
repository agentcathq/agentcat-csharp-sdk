using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCat.Internal.Errors;
using AgentCat.Internal.Handles;
using AgentCat.Internal.Text;
using AgentCat.Internal.Tokens;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace AgentCat.Internal.Engine;

internal sealed class ToolCallEvent
{
    private const string UnknownTool = "Unknown Tool";

    private static readonly string SeamType = typeof(CallToolSeam).FullName!;

    private static readonly string SeamFrameMarker = " " + SeamType + ".";

    private readonly JsonNode? _rawParams;
    private readonly string? _toolName;
    private readonly DateTimeOffset _start;
    private readonly Dictionary<string, string> _tags;
    private readonly IReadOnlyDictionary<string, JsonElement>? _arguments;
    private readonly ExtraSnapshot _extra;
    private readonly string _serverName;
    private readonly string _serverVersion;
    private readonly string? _clientName;
    private readonly string? _clientVersion;
    private readonly PendingHooks? _hooks;
    private CallToolResult? _result;
    private Exception? _exception;
    private Exception? _converted;
    private string? _convertedName;

    internal ToolCallEvent(
        JsonNode? rawParams,
        string? toolName,
        DateTimeOffset start,
        HandleResolution resolution,
        Dictionary<string, string> tags,
        string? userIntent,
        IReadOnlyDictionary<string, JsonElement>? arguments,
        ExtraSnapshot extra,
        (string Name, string Version) server,
        (string? Name, string? Version) client,
        PendingHooks? hooks)
    {
        _hooks = hooks;
        _arguments = arguments;
        _extra = extra;
        _serverName = server.Name;
        _serverVersion = server.Version;
        _clientName = client.Name;
        _clientVersion = client.Version;
        _rawParams = rawParams;
        _toolName = toolName;
        _start = start;
        _tags = tags;
        Resolution = resolution;
        UserIntent = userIntent;
    }

    internal HandleResolution Resolution { get; }

    internal string? UserIntent { get; }

    internal PendingHooks? Hooks => _hooks;

    // For a call whose event is never published: stops the hooks that are still running.
    internal void Abandon()
    {
        try
        {
            _hooks?.Abandon();
        }
        catch (Exception)
        {
        }
    }

    internal void Publish(AgentCatTracker tracker, CallToolResult? result, Exception? stashed = null)
    {
        var isError = result?.IsError == true;
        _result = result;
        _exception = isError ? stashed : null;
        Enqueue(tracker, isError);
    }

    internal void PublishThrown(AgentCatTracker tracker, Exception exception, Exception? converted, string? convertedName)
    {
        _exception = exception;
        _converted = converted;
        _convertedName = convertedName;
        Enqueue(tracker, isError: true);
    }

    internal void PublishInputRequired(AgentCatTracker tracker)
    {
        _tags[WireNames.TagMrtr] = WireNames.MrtrInputRequired;
        Enqueue(tracker, isError: false);
    }

    private void Enqueue(AgentCatTracker tracker, bool isError)
    {
        var e = new AgentCatEvent
        {
            EventType = WireNames.ToolCallEventType,
            ResourceName = string.IsNullOrEmpty(_toolName) ? UnknownTool : _toolName,
            SessionId = Resolution.SessionId,
            Timestamp = _start,
            Tags = _tags,
            UserIntent = UserIntent,
            IsError = isError ? true : null,
            ServerName = _serverName,
            ServerVersion = _serverVersion,
            ClientName = _clientName,
            ClientVersion = _clientVersion,
        };
        EventPublisher.Publish(tracker, e, PrepareAsync, redactStrings: true, _hooks is null ? null : _hooks.Abandon);
    }

    private async ValueTask PrepareAsync(AgentCatEvent e, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        if (_hooks is not null)
        {
            try
            {
                await _hooks.ApplyAsync(e, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                failure = ex;
            }
        }

        var result = _result;
        if (_converted is not null)
        {
            Run(ref failure, () => result = ConvertedResult(_convertedName, _converted));
        }

        e.InputTokens = TokenEstimator.EstimateInputTokens(_arguments);
        e.OutputTokens = TokenEstimator.EstimateOutputTokens(result);
        Run(ref failure, () => e.Parameters = new JsonObject
        {
            ["request"] = new JsonObject
            {
                ["method"] = "tools/call",
                ["params"] = EventNodes.FromJson(_rawParams),
            },
            ["extra"] = ExtraProjection.Project(_extra),
        });
        Run(ref failure, () => e.Response = result is null ? null : EventNodes.FromResult(result));
        if (_exception is not null)
        {
            e.Error = CaptureToSeam(_exception);
        }
        else if (result?.IsError == true)
        {
            e.Error = ExceptionCapture.CaptureFromResult(e.Response);
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    // An exception rethrown by the seam keeps collecting frames as it moves on, possibly while the pipeline reads it,
    // so the captured frames and stack end at the seam's own frame, which is the last one when the seam catches it.
    private static JsonObject CaptureToSeam(Exception exception)
    {
        var error = ExceptionCapture.Capture(exception);
        if (error["frames"] is JsonArray frames)
        {
            for (var i = 0; i < frames.Count; i++)
            {
                if (frames[i] is JsonObject frame
                    && frame["module"] is JsonValue module
                    && module.TryGetValue<string>(out var name)
                    && name == SeamType)
                {
                    while (frames.Count > i + 1)
                    {
                        frames.RemoveAt(frames.Count - 1);
                    }

                    break;
                }
            }
        }

        if (error["stack"] is JsonValue stack && stack.TryGetValue<string>(out var text))
        {
            error["stack"] = StackToSeam(text, exception);
        }

        return error;
    }

    // Cuts the exception's own frame lines after the seam's frame and keeps what follows them, such as an
    // AggregateException's inner sections; frames of inner exceptions are never cut.
    private static string StackToSeam(string stack, Exception exception)
    {
        var (start, end) = ExceptionLayout.OwnFrames(stack, exception) ?? (0, stack.Length);
        while (start < end)
        {
            var newline = stack.IndexOf('\n', start, end - start);
            var lineEnd = newline < 0 ? end : newline;
            if (lineEnd > start && stack[lineEnd - 1] == '\r')
            {
                lineEnd--;
            }

            if (lineEnd > start
                && stack[start] == ' '
                && stack.IndexOf(SeamFrameMarker, start, lineEnd - start, StringComparison.Ordinal) >= 0)
            {
                return stack.Remove(lineEnd, end - lineEnd);
            }

            if (newline < 0)
            {
                break;
            }

            start = newline + 1;
        }

        return stack;
    }

    private static CallToolResult ConvertedResult(string? name, Exception exception)
        => new()
        {
            IsError = true,
            Content = new List<ContentBlock>
            {
                new TextContentBlock
                {
                    Text = exception is McpException
                        ? "An error occurred invoking '" + name + "': " + exception.Message
                        : "An error occurred invoking '" + name + "'.",
                },
            },
        };

    private static void Run(ref Exception? failure, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            failure ??= ex;
        }
    }
}
