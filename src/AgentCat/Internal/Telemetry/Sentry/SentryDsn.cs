using System;
using System.Text.RegularExpressions;

namespace AgentCat.Internal.Telemetry.Sentry;

// TS parseDSN: the envelope endpoint and the public key of a DSN, protocol://publicKey@host[:port][/path]/projectId.
internal sealed class SentryDsn
{
    // TS /^(https?):\/\/([a-f0-9]+)@([\w.-]+)(:\d+)?(\/.*)?\/(\d+)$/ with its JS meaning: \w and \d are ASCII only,
    // "." stops at \n, \r, U+2028 and U+2029, and $ is the very end (.NET's $ also matches before a final \n).
    private static readonly Regex Pattern = new(
        @"\A(https?)://([a-f0-9]+)@([A-Za-z0-9_.-]+)(:[0-9]+)?(/[^\n\r\u2028\u2029]*)?/([0-9]+)\z",
        RegexOptions.CultureInvariant);

    private SentryDsn(string endpoint, string publicKey)
    {
        Endpoint = endpoint;
        PublicKey = publicKey;
    }

    // TS `${protocol}://${host}${port ? `:${port}` : ""}${path}/api/${projectId}/envelope/`.
    internal string Endpoint { get; }

    internal string PublicKey { get; }

    /// <summary>The DSN's endpoint and key; a DSN the TS regex rejects throws <c>Error: Invalid Sentry DSN: …</c>, masked.</summary>
    internal static SentryDsn Parse(string dsn)
    {
        var match = Pattern.Match(dsn);
        if (!match.Success)
        {
            throw new JsError("Error", "Invalid Sentry DSN: " + Masked(dsn));
        }

        var groups = match.Groups;
        return new SentryDsn(
            groups[1].Value + "://" + groups[3].Value + groups[4].Value + groups[5].Value + "/api/" + groups[6].Value + "/envelope/",
            groups[2].Value);
    }

    // The DSN with its user-info as "***" (https://***@host/1), so neither the public key nor the secret of a legacy
    // key:secret DSN reaches a log line or diagnostics (TS prints the whole DSN). The user-info runs from the scheme's
    // "://" (or the start) to the last "@"; scheme, host, port and path stay. Without an "@", or with nothing before
    // it, the DSN is returned as it is.
    private static string Masked(string dsn)
    {
        var at = dsn.LastIndexOf('@');
        if (at < 0)
        {
            return dsn;
        }

        var scheme = dsn.Substring(0, at).IndexOf("://", StringComparison.Ordinal);
        var start = scheme < 0 ? 0 : scheme + 3;
        return start == at ? dsn : dsn.Remove(start, at - start).Insert(start, "***");
    }
}
