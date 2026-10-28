using System;
using AgentCat.Internal.Text;

namespace AgentCat.Internal.Telemetry;

// An error that the TS exporters build themselves (`new Error(…)`, a TypeError of JS semantics). Its log text keeps
// the JS name, as TS `${error}` prints it; the manager's export line uses the message only, as TS `error.message`.
internal sealed class JsError : Exception
{
    internal JsError(string name, string message)
        : base(message)
    {
        Name = name;
    }

    internal string Name { get; }

    // TS `${error}`: "Error: …" for an error the SDK builds, "{type}: {message}" for a platform exception.
    internal static string Text(Exception error) => error is JsError js ? js.Name + ": " + js.Message : ErrorText.Of(error);
}
