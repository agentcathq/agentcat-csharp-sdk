using System;
using AgentCat.Internal.Text;

namespace AgentCat.Internal.Diagnostics;

/// <summary>
/// Whether SDK diagnostics run. The option and <c>DISABLE_DIAGNOSTICS</c> can only turn them off,
/// <c>AGENTCAT_DISABLE_DIAGNOSTICS</c> can also force them on in a test run, and <c>AGENTCAT_ENABLED</c> has no say.
/// </summary>
internal static class DiagnosticsSwitch
{
    internal const string PrefixedVariable = "AGENTCAT_DISABLE_DIAGNOSTICS";
    internal const string UnprefixedVariable = "DISABLE_DIAGNOSTICS";

    private enum Flag
    {
        Unset,
        ForceEnabled,
        Disabled,
    }

    /// <summary>Never throws. A variable that cannot be read counts as unset.</summary>
    internal static bool IsEnabled(bool disableOption, Func<string, string?> environment, bool inTestRun)
    {
        if (disableOption)
        {
            return false;
        }

        var flag = ReadFlag(environment, UnprefixedVariable) == Flag.Disabled
            ? Flag.Disabled
            : ReadFlag(environment, PrefixedVariable);
        return flag != Flag.Disabled && (flag == Flag.ForceEnabled || !inTestRun);
    }

    private static Flag ReadFlag(Func<string, string?> environment, string name)
    {
        string? raw;
        try
        {
            raw = environment(name);
        }
        catch (Exception)
        {
            return Flag.Unset;
        }

        if (raw is null)
        {
            return Flag.Unset;
        }

        switch (JsSemantics.Trim(raw).ToLowerInvariant())
        {
            case "":
                return Flag.Unset;
            case "false":
            case "0":
            case "no":
            case "off":
                return Flag.ForceEnabled;
            default:
                return Flag.Disabled;
        }
    }
}
