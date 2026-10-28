using System;
using System.Threading;
using AgentCat.Internal.Logging;

namespace AgentCat.Internal.Runtime;

/// <summary>
/// Whether AgentCat installs and publishes. <c>AGENTCAT_ENABLED</c> decides when it holds one of the words; otherwise
/// AgentCat is off in a test run and on everywhere else.
/// </summary>
internal sealed class EnabledGate
{
    internal const string Variable = "AGENTCAT_ENABLED";

    private static int _unknownReported;
    private static int _skippedAnInstall;

    private readonly Func<string, string?> _environment;
    private readonly Func<bool> _inTestRun;
    private readonly IAgentCatLog _log;

    // Set once an install passed this gate on the detection: the process is no test run, and later checks skip the
    // detection. The variable is still read on every call.
    private int _notATestRun;

    internal EnabledGate(Func<string, string?> environment, Func<bool> inTestRun, IAgentCatLog log)
    {
        _environment = environment;
        _inTestRun = inTestRun;
        _log = log;
    }

    /// <summary>True once a gate stopped an install in this process.</summary>
    internal static bool SkippedAnInstall => Volatile.Read(ref _skippedAnInstall) != 0;

    // Test seam: forgets that an install was stopped.
    internal static void ResetSkippedAnInstall() => Interlocked.Exchange(ref _skippedAnInstall, 0);

    // Test seam: lets the once-per-process unknown-value line be written again.
    internal static void ResetUnknownValueWarning() => Interlocked.Exchange(ref _unknownReported, 0);

    /// <summary>Reads the variable on every call. Never throws.</summary>
    internal bool IsEnabled() => IsEnabled(out _);

    /// <summary><see cref="IsEnabled()"/> for an install: a stopped install is remembered for the process.</summary>
    internal bool AllowsInstall()
    {
        if (IsEnabled(out var byDetection))
        {
            if (byDetection)
            {
                Volatile.Write(ref _notATestRun, 1);
            }

            return true;
        }

        Interlocked.Exchange(ref _skippedAnInstall, 1);
        return false;
    }

    // byDetection is true when the variable held no word and the test-run detection decided.
    private bool IsEnabled(out bool byDetection)
    {
        byDetection = false;
        var raw = Read();
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "true":
            case "1":
            case "yes":
            case "on":
                return true;
            case "false":
            case "0":
            case "no":
            case "off":
                return false;
            case null:
            case "":
                break;
            default:
                ReportUnknown(raw!);
                break;
        }

        if (Volatile.Read(ref _notATestRun) != 0)
        {
            return true;
        }

        byDetection = true;
        return !InTestRun();
    }

    private string? Read()
    {
        try
        {
            return _environment(Variable);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // A check that fails counts as no test run: a production server must never go silent by mistake.
    private bool InTestRun()
    {
        try
        {
            return _inTestRun();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void ReportUnknown(string value)
    {
        if (Interlocked.Exchange(ref _unknownReported, 1) != 0)
        {
            return;
        }

        try
        {
            _log.Write("WARN: " + Variable + " \"" + value + "\" is not one of true, 1, yes, on, false, 0, no, off; the default is used");
        }
        catch (Exception)
        {
        }
    }
}
