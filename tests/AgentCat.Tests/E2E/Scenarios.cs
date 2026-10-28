using System.Text.Json.Nodes;

namespace AgentCat.Tests.E2E;

// The TS e2e-http scenario table (names, options and fixture values). Compiled into the stdio server too, which reads
// the scenario name from AGENTCAT_E2E_SCENARIO.
internal static class Scenarios
{
    internal const string BaselineDefaults = "baseline-defaults";
    internal const string TracingOff = "tracing-off";
    internal const string TracingOffXReportMissingOn = "tracing-off-x-report-missing-on";
    internal const string ReportMissingOff = "report-missing-off";
    internal const string ReportMissingCall = "report-missing-call";
    internal const string ContextOff = "context-off";
    internal const string ContextCustomDescription = "context-custom-description";
    internal const string AgentTrackingMint = "agent-tracking-mint";
    internal const string AgentTrackingSupplied = "agent-tracking-supplied";
    internal const string AgentTrackingXHook = "agent-tracking-x-hook";
    internal const string TaskHookDeterministic = "task-hook-deterministic";
    internal const string TaskHookNull = "task-hook-null";
    internal const string TaskSuppliedContinuity = "task-supplied-continuity";
    internal const string MintBackStructuredMirror = "mint-back-structured-mirror";
    internal const string StrictSchemaInjection = "strict-schema-injection";
    internal const string StripProofLoose = "strip-proof-loose";
    internal const string ErrorPath = "error-path";
    internal const string HooksIdentityTagsProps = "hooks-identity-tags-props";
    internal const string Redaction = "redaction";
    internal const string ClientIdentityLadder = "client-identity-ladder";
    internal const string RebuildOnDemand = "rebuild-on-demand";
    internal const string SessionContinuityStateful = "session-continuity-stateful";

    internal const string SessionHookValue = "corr-7";
    internal const string AgentSessionHookValue = "corr-agent-7";
    internal const string CustomContextDescriptionValue = "why?";
    internal const string RedactionSecret = "SECRET_TOKEN";
    internal const string RedactionReplacement = "[REDACTED]";

    // The redaction scenario also rewrites these, so a session_id or resource_name that went through the redactor
    // would change.
    internal const string RedactionSessionPrefix = "ses_";
    internal const string RedactionToolName = "echo";

    internal const string IdentityUserId = "e2e-user-1";
    internal const string IdentityUserName = "E2E User";
    internal const string IdentityUserDataPlan = "enterprise";
    internal const string CustomerTagKey = "e2e_customer_tag";
    internal const string CustomerTagValue = "customer-tag-present";
    internal const string CollidingTagValue = "customer-collision-loses";
    internal const string CustomerPropKey = "e2e_customer_prop";
    internal const string CustomerPropValue = "prop-value";
    internal const string SessionSourceTag = "agentcat_session_id_source";

    internal static IReadOnlyList<string> Names { get; } = new[]
    {
        BaselineDefaults,
        TracingOff,
        TracingOffXReportMissingOn,
        ReportMissingOff,
        ReportMissingCall,
        ContextOff,
        ContextCustomDescription,
        AgentTrackingMint,
        AgentTrackingSupplied,
        AgentTrackingXHook,
        TaskHookDeterministic,
        TaskHookNull,
        TaskSuppliedContinuity,
        MintBackStructuredMirror,
        StrictSchemaInjection,
        StripProofLoose,
        ErrorPath,
        HooksIdentityTagsProps,
        Redaction,
        ClientIdentityLadder,
        RebuildOnDemand,
        SessionContinuityStateful,
    };

    internal static void Apply(string name, AgentCatOptions options)
    {
        switch (name)
        {
            case TracingOff:
            case TracingOffXReportMissingOn:
                options.EnableTracing = false;
                break;
            case ReportMissingOff:
                options.EnableReportMissing = false;
                break;
            case ContextOff:
                options.EnableToolCallContext = false;
                break;
            case ContextCustomDescription:
                options.CustomContextDescription = CustomContextDescriptionValue;
                break;
            case AgentTrackingMint:
            case AgentTrackingSupplied:
                options.EnableAgentTracking = true;
                break;
            case AgentTrackingXHook:
                options.EnableAgentTracking = true;
                options.ResolveSessionId = static (_, _) => new ValueTask<string?>(AgentSessionHookValue);
                break;
            case TaskHookDeterministic:
                options.ResolveSessionId = static (_, _) => new ValueTask<string?>(SessionHookValue);
                break;
            case TaskHookNull:
                options.ResolveSessionId = static (_, _) => new ValueTask<string?>((string?)null);
                break;
            case HooksIdentityTagsProps:
                options.Identify = static (_, _) => new ValueTask<UserIdentity?>(new UserIdentity(IdentityUserId)
                {
                    UserName = IdentityUserName,
                    UserData = new JsonObject { ["plan"] = IdentityUserDataPlan },
                });
                options.EventTags = static (_, _) => new ValueTask<IReadOnlyDictionary<string, string>?>(new Dictionary<string, string>
                {
                    [SessionSourceTag] = CollidingTagValue,
                    [CustomerTagKey] = CustomerTagValue,
                });
                options.EventProperties = static (_, _) => new ValueTask<JsonObject?>(new JsonObject { [CustomerPropKey] = CustomerPropValue });
                break;
            case Redaction:
                options.RedactSensitiveInformation = static (text, _) => new ValueTask<string>(text
                    .Replace(RedactionSecret, RedactionReplacement, StringComparison.Ordinal)
                    .Replace(RedactionSessionPrefix, RedactionReplacement, StringComparison.Ordinal)
                    .Replace(RedactionToolName, RedactionReplacement, StringComparison.Ordinal));
                break;
            case BaselineDefaults:
            case ReportMissingCall:
            case TaskSuppliedContinuity:
            case MintBackStructuredMirror:
            case StrictSchemaInjection:
            case StripProofLoose:
            case ErrorPath:
            case ClientIdentityLadder:
            case RebuildOnDemand:
            case SessionContinuityStateful:
                break;
            default:
                throw new ArgumentException("Unknown e2e scenario: " + name, nameof(name));
        }
    }
}
