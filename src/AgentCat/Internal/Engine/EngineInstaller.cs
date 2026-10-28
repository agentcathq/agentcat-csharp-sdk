using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using AgentCat.Internal.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Internal.Engine;

internal static class EngineInstaller
{
    internal const string AlternateHandlerWarning =
        "Warning: AgentCat did not instrument this server: Handlers.CallToolWithAlternateHandler is set, and McpServer.Create rejects tools/call filters with it.";

    private static readonly ConditionalWeakTable<McpServerOptions, AgentCatTracker> Installed = new();
    private static readonly ConditionalWeakTable<AgentCatTracker, object> Warned = new();
    private static readonly object Gate = new();
    private static readonly PropertyInfo? AlternateHandler = FindAlternateHandler();

    internal enum Outcome
    {
        Installed,
        AlreadyInstalled,
        AlternateHandler,
        SetupFailed,
        Failed,
    }

    // Runs for every options instance that stateless HTTP builds: it reuses the tracker's filters and logs nothing on success.
    internal static bool Install(McpServerOptions options, AgentCatTracker tracker)
    {
        var outcome = TryInstall(options, tracker, out var failure);
        string? warning = null;
        if (failure is not null)
        {
            warning = "Warning: Failed to track server - " + ErrorText.Of(failure);
        }
        else if (outcome == Outcome.AlternateHandler && FirstAlternateHandlerWarning(tracker))
        {
            warning = AlternateHandlerWarning;
        }

        if (warning is not null)
        {
            tracker.Runtime.CallLog.Write(warning);
        }

        return outcome == Outcome.Installed;
    }

    // Checks and installs under one lock, so of two concurrent installs on one options instance exactly one installs
    // and the other sees AlreadyInstalled. Logs nothing.
    internal static Outcome TryInstall(McpServerOptions options, AgentCatTracker tracker, out Exception? failure)
    {
        failure = null;
        try
        {
            if (tracker.SetupFailed)
            {
                return Outcome.SetupFailed;
            }

            lock (Gate)
            {
                if (IsInstalled(options))
                {
                    return Outcome.AlreadyInstalled;
                }

                if (HasAlternateHandler(options))
                {
                    return Outcome.AlternateHandler;
                }

                AddSeams(options, tracker.Seams);
                Installed.Add(options, tracker);
                return Outcome.Installed;
            }
        }
        catch (Exception ex)
        {
            failure = ex;
            return Outcome.Failed;
        }
    }

    internal static bool IsInstalled(McpServerOptions options) => Installed.TryGetValue(options, out _);

    internal static bool TryGetTracker(McpServerOptions options, [NotNullWhen(true)] out AgentCatTracker? tracker)
        => Installed.TryGetValue(options, out tracker);

    internal static bool HasAlternateHandler(McpServerOptions options) => AlternateHandler?.GetValue(options.Handlers) is not null;

    private static bool FirstAlternateHandlerWarning(AgentCatTracker tracker)
    {
        lock (Gate)
        {
            if (Warned.TryGetValue(tracker, out _))
            {
                return false;
            }

            Warned.Add(tracker, Gate);
            return true;
        }
    }

    private static void AddSeams(McpServerOptions options, EngineSeams seams)
    {
        var request = options.Filters.Request;
        var message = options.Filters.Message;
        try
        {
            request.ListToolsFilters.Insert(0, seams.List);
            request.CallToolFilters.Insert(0, seams.Outer);
            request.CallToolFilters.Add(seams.Inner);
            message.OutgoingFilters.Insert(0, seams.Outgoing);
        }
        catch (Exception)
        {
            TryRemove(request.ListToolsFilters, seams.List);
            TryRemove(request.CallToolFilters, seams.Outer);
            TryRemove(request.CallToolFilters, seams.Inner);
            TryRemove(message.OutgoingFilters, seams.Outgoing);
            throw;
        }
    }

    private static void TryRemove<T>(IList<T> filters, T filter)
    {
        try
        {
            filters.Remove(filter);
        }
        catch (Exception)
        {
        }
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(McpServerHandlers))]
    private static PropertyInfo? FindAlternateHandler()
        => typeof(McpServerHandlers).GetProperty("CallToolWithAlternateHandler");
}
