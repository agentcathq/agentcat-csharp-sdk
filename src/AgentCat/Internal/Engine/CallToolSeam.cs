using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using AgentCat.Internal.Handles;
using AgentCat.Internal.Http;
using AgentCat.Internal.Injection;
using AgentCat.Internal.Json;
using AgentCat.Internal.Runtime;
using AgentCat.Internal.Text;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Internal.Engine;

internal sealed class CallToolSeam
{
    internal const string ExceptionItem = "agentcat.exception";

    internal const string SessionTextItem = "agentcat.sessionText";

    private const string InputRequiredExceptionName = "ModelContextProtocol.Protocol.InputRequiredException";

    private const string InputResponsesParam = "inputResponses";

    private const string TaskParam = "task";

    private const string MetaParam = "_meta";

    private const string ClientCapabilitiesKey = "io.modelcontextprotocol/clientCapabilities";

    private const string ExtensionsKey = "extensions";

    private const string TasksExtension = "io.modelcontextprotocol/tasks";

    private static readonly JsonTypeInfo ResultInfo = McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(CallToolResult));

    private static readonly bool CoreTasks = typeof(McpServer).Assembly.GetName().Version?.Major == 1;

    internal CallToolSeam(AgentCatTracker tracker) => Tracker = tracker;

    internal AgentCatTracker Tracker { get; }

    internal McpRequestHandler<CallToolRequestParams, CallToolResult> Wrap(McpRequestHandler<CallToolRequestParams, CallToolResult> next)
        => (context, cancellationToken) => InvokeAsync(Tracker, next, context, cancellationToken);

    private static ValueTask<CallToolResult> InvokeAsync(
        AgentCatTracker tracker,
        McpRequestHandler<CallToolRequestParams, CallToolResult> next,
        RequestContext<CallToolRequestParams> context,
        CancellationToken cancellationToken)
    {
        ClearItems(context);
        DateTimeOffset? start = null;
        if (tracker.Settings.EnableTracing)
        {
            start = tracker.Runtime.Clock.UtcNow;
        }
        else if (Intercepts(tracker, context.Params?.Name) && TryReply(tracker, context, out var reply))
        {
            return new ValueTask<CallToolResult>(reply);
        }

        return RegistryRebuilder.IsNeeded(tracker.Registries, context)
            ? RebuildThenRunAsync(tracker, next, context, start, cancellationToken)
            : Run(tracker, next, context, start, cancellationToken);
    }

    private static async ValueTask<CallToolResult> RebuildThenRunAsync(
        AgentCatTracker tracker,
        McpRequestHandler<CallToolRequestParams, CallToolResult> next,
        RequestContext<CallToolRequestParams> context,
        DateTimeOffset? start,
        CancellationToken cancellationToken)
    {
        await RegistryRebuilder.EnsureAsync(tracker, context, cancellationToken).ConfigureAwait(false);
        return await Run(tracker, next, context, start, cancellationToken).ConfigureAwait(false);
    }

    // Serves the traced (start set), tracing-off and degraded paths: the tool never gets an injected argument,
    // and a failed strip runs it with the arguments as received rather than failing the call.
    private static ValueTask<CallToolResult> Run(
        AgentCatTracker tracker,
        McpRequestHandler<CallToolRequestParams, CallToolResult> next,
        RequestContext<CallToolRequestParams> context,
        DateTimeOffset? start,
        CancellationToken cancellationToken)
    {
        var name = context.Params?.Name;
        var arguments = context.Params?.Arguments;
        IDictionary<string, JsonElement>? stripped;
        var stripFailed = false;
        try
        {
            tracker.OnCallStage?.Invoke("strip");
            stripped = ArgumentStripper.Strip(arguments, name, tracker.Registries.InjectedParams);
        }
        catch (Exception ex)
        {
            LogTracingFailed(tracker, name, ex);
            stripped = arguments;
            stripFailed = true;
        }

        ToolCallEvent? call = null;
        if (start is { } started)
        {
            call = stripFailed ? null : Prepare(tracker, context, name, started);
            if (Intercepts(tracker, name) && TryReply(tracker, context, out var reply))
            {
                return new ValueTask<CallToolResult>(call is null ? reply : Finish(tracker, context, name, call, reply, decorate: true));
            }
        }

        return call is null && ReferenceEquals(stripped, arguments)
            ? next(context, cancellationToken)
            : CallAsync(tracker, next, context, name, call, stripped, cancellationToken);
    }

    private static async ValueTask<CallToolResult> CallAsync(
        AgentCatTracker tracker,
        McpRequestHandler<CallToolRequestParams, CallToolResult> next,
        RequestContext<CallToolRequestParams> context,
        string? name,
        ToolCallEvent? call,
        IDictionary<string, JsonElement>? stripped,
        CancellationToken cancellationToken)
    {
        var parameters = context.Params;
        var original = parameters?.Arguments;
        var swap = parameters is not null && !ReferenceEquals(stripped, original);
        var taskAugmented = IsTaskAugmented(context);
        CallToolResult result;
        if (swap)
        {
            parameters!.Arguments = stripped;
        }

        try
        {
            result = call is null
                ? await next(context, cancellationToken).ConfigureAwait(false)
                : await RunEnclosedAsync(next, context, call, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (call is not null)
            {
                Fail(tracker, context, name, call, ex, cancellationToken);
            }

            throw;
        }
        finally
        {
            if (swap && !taskAugmented)
            {
                parameters!.Arguments = original;
            }
        }

        return call is null ? result : Finish(tracker, context, name, call, result, !taskAugmented);
    }

    // The handler, and the work it starts, run with the call as their enclosing tool call. The value lives in this
    // method's execution context only, so it never reaches the post-handler stages or the code that invoked the seam.
    private static async ValueTask<CallToolResult> RunEnclosedAsync(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next,
        RequestContext<CallToolRequestParams> context,
        ToolCallEvent call,
        CancellationToken cancellationToken)
    {
        EnclosingToolCall.Enter(context.Server, call.Resolution, call.Hooks);
        return await next(context, cancellationToken).ConfigureAwait(false);
    }

    private static ToolCallEvent? Prepare(AgentCatTracker tracker, RequestContext<CallToolRequestParams> context, string? name, DateTimeOffset start)
    {
        PendingHooks? hooks = null;
        try
        {
            tracker.OnCallStage?.Invoke("prepare");
            var request = OptsInToTasks(context) || RunsOnSessionContext(context) ? null : AspNetCoreRequest.Capture(context.Services, tracker.Runtime.CallLog);
            var settings = tracker.Settings;
            var arguments = context.Params?.Arguments;
            var resolution = HandleResolver.Resolve(
                ReadOnly(arguments),
                settings.ResolveSessionId is not null,
                settings.EnableAgentTracking,
                string.IsNullOrEmpty(name) || !tracker.State.IsSessionParamDeclared(name!),
                start);
            hooks = PendingHooks.AnyConfigured(settings)
                ? PendingHooks.Start(settings, ToolCallSnapshot.Build(context, request), tracker.Runtime.CallLog, tracker.Runtime.Clock)
                : null;
            var intent = Intercepts(tracker, name) || (settings.EnableToolCallContext && name != WireNames.GetMoreToolsName)
                ? Intent(arguments)
                : null;
            return new ToolCallEvent(context.JsonRpcRequest.Params, name, start, resolution, Tags(context, resolution), intent,
                arguments is null ? null : ReadOnly(arguments), ExtraProjection.Capture(context, request), ServerIdentity.Of(context.Server?.ServerOptions), ClientIdentity.For(context), hooks);
        }
        catch (Exception ex)
        {
            hooks?.Abandon();
            LogTracingFailed(tracker, name, ex);
            return null;
        }
    }

    private static void LogTracingFailed(AgentCatTracker tracker, string? name, Exception exception)
        => tracker.Runtime.CallLog.Write(
            "Warning: AgentCat tracing failed for tool " + (name ?? "undefined") + ", falling back to original handler - " + ErrorText.Of(exception));

    private static CallToolResult Finish(
        AgentCatTracker tracker,
        RequestContext<CallToolRequestParams> context,
        string? name,
        ToolCallEvent call,
        CallToolResult result,
        bool decorate)
    {
        try
        {
            tracker.OnCallStage?.Invoke("finish");
            var final = result;
            if (decorate && result is not null)
            {
                var resolution = call.Resolution;
                final = ResultDecorator.Decorate(
                    result,
                    MintBack.BuildText(resolution),
                    MintBack.BuildStructured(resolution),
                    tracker.Registries.MirrorsStructured(name ?? string.Empty));
            }

            call.Publish(tracker, result, result?.IsError == true ? TakeStash(context) : null);
            return final;
        }
        catch (Exception ex)
        {
            call.Abandon();
            LogPostHandlerFailure(tracker, name, ex);
            return result;
        }
    }

    private static void Fail(
        AgentCatTracker tracker,
        RequestContext<CallToolRequestParams> context,
        string? name,
        ToolCallEvent call,
        Exception exception,
        CancellationToken cancellationToken)
    {
        try
        {
            tracker.OnCallStage?.Invoke("fail");
            var stashed = TakeStash(context);
            var kind = Classify(exception, cancellationToken);
            if (kind == ThrownKind.InputRequired)
            {
                call.PublishInputRequired(tracker);
                return;
            }

            var converted = kind == ThrownKind.Converted;
            if (converted && MintBack.BuildText(call.Resolution) is { } text)
            {
                context.Items[SessionTextItem] = text;
            }

            call.PublishThrown(tracker, stashed ?? exception, converted ? exception : null, context.Params?.Name);
        }
        catch (Exception ex)
        {
            call.Abandon();
            LogPostHandlerFailure(tracker, name, ex);
        }
    }

    private static ThrownKind Classify(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return ThrownKind.Cancelled;
        }

        if (exception is McpProtocolException)
        {
            return ThrownKind.Protocol;
        }

        for (var type = exception.GetType(); type is not null; type = type.BaseType)
        {
            if (type.FullName == InputRequiredExceptionName)
            {
                return ThrownKind.InputRequired;
            }
        }

        return ThrownKind.Converted;
    }

    private static Exception? TakeStash(RequestContext<CallToolRequestParams> context)
    {
        try
        {
            if (context.JsonRpcRequest.Context?.Items is not { Count: > 0 } items || !items.TryGetValue(ExceptionItem, out var value))
            {
                return null;
            }

            items.Remove(ExceptionItem);
            return value as Exception;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void LogPostHandlerFailure(AgentCatTracker tracker, string? name, Exception exception)
        => tracker.Runtime.CallLog.Write(
            "Warning: AgentCat post-handler processing failed for tool " + (name ?? "undefined") + "; returning the customer's result untouched - " + ErrorText.Of(exception));

    private static bool Intercepts(AgentCatTracker tracker, string? name)
        => tracker.Settings.EnableReportMissing && name == WireNames.GetMoreToolsName;

    private static bool TryReply(AgentCatTracker tracker, RequestContext<CallToolRequestParams> context, out CallToolResult reply)
    {
        try
        {
            var arguments = context.Params?.Arguments;
            var text = arguments is not null
                && arguments.TryGetValue(WireNames.ContextParam, out var value)
                && value.ValueKind == JsonValueKind.String
                    ? JsJson.ReadString(value)
                    : null;
            reply = (CallToolResult)JsonSerializer.Deserialize(GetMoreTools.Reply(text, tracker.Runtime.CallLog), ResultInfo)!;
            return true;
        }
        catch (Exception)
        {
            reply = null!;
            return false;
        }
    }

    private static string? Intent(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || !arguments.TryGetValue(WireNames.ContextParam, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => JsJson.ReadString(value),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.GetRawText(),
        };
    }

    private static IReadOnlyDictionary<string, JsonElement>? ReadOnly(IDictionary<string, JsonElement>? arguments)
        => arguments as IReadOnlyDictionary<string, JsonElement>
            ?? (arguments is null ? null : new ReadOnlyDictionary<string, JsonElement>(arguments));

    private static Dictionary<string, string> Tags(RequestContext<CallToolRequestParams> context, HandleResolution resolution)
    {
        var handles = HandleTags.Build(resolution, ClientIdentity.ProtocolVersion(context));
        if (RawParam(context, InputResponsesParam) is not (JsonObject or JsonArray))
        {
            return handles;
        }

        var tags = new Dictionary<string, string>(StringComparer.Ordinal) { [WireNames.TagMrtr] = WireNames.MrtrContinuation };
        foreach (var tag in handles)
        {
            tags[tag.Key] = tag.Value;
        }

        return tags;
    }

    // A 1.x core task returns from the filters at task creation while the tool runs on in the background on this
    // same context, so its arguments stay stripped and the task handle goes out as the SDK built it.
    private static bool IsTaskAugmented(RequestContext<CallToolRequestParams> context)
        => CoreTasks
            && context.MatchedPrimitive is McpServerTool
            && RawParam(context, TaskParam) is JsonObject;

    // Stateful streamable HTTP runs a call on the execution context of the POST that carried it, which the transport
    // hands over in the message context. With PerSessionExecutionContext it hands over none, and the call runs on the
    // session's context, where IHttpContextAccessor returns the request that started the session until ASP.NET Core
    // disposes that request, after its response. Only streamable HTTP sets RelatedTransport, and only its stateful
    // transport has a session ID: a stateless session runs inside its POST, and a legacy SSE session inside its GET.
    private static bool RunsOnSessionContext(RequestContext<CallToolRequestParams> context)
    {
        var message = context.JsonRpcRequest.Context;
        return message?.RelatedTransport?.SessionId is not null && message.ExecutionContext is null;
    }

    // The 2.x Tasks extension runs a call whose client opts in to tasks on a Task.Run that it starts before the POST
    // returns, so the request can end, and its context serve the next request on the connection, while it is read.
    // The opt-in is the extension key in the client capabilities of _meta, as the Tasks extension reads it.
    private static bool OptsInToTasks(RequestContext<CallToolRequestParams> context)
    {
        if (CoreTasks)
        {
            return false;
        }

        try
        {
            return RawParam(context, MetaParam) is JsonObject meta
                && meta.TryGetPropertyValue(ClientCapabilitiesKey, out var capabilities)
                && capabilities is JsonObject capabilitiesObject
                && capabilitiesObject.TryGetPropertyValue(ExtensionsKey, out var extensions)
                && extensions is JsonObject extensionsObject
                && extensionsObject.ContainsKey(TasksExtension);
        }
        catch (Exception)
        {
            return true;
        }
    }

    // Reading a raw object whose keys collide throws, while the SDK's typed params of the same request do not.
    private static JsonNode? RawParam(RequestContext<CallToolRequestParams> context, string name)
    {
        try
        {
            return context.JsonRpcRequest.Params is JsonObject raw && raw.TryGetPropertyValue(name, out var value) ? value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void ClearItems(RequestContext<CallToolRequestParams> context)
    {
        try
        {
            if (context.JsonRpcRequest.Context?.Items is { Count: > 0 } items)
            {
                items.Remove(ExceptionItem);
                items.Remove(SessionTextItem);
            }
        }
        catch (Exception)
        {
        }
    }

    private enum ThrownKind
    {
        Converted,
        Protocol,
        Cancelled,
        InputRequired,
    }
}
