namespace AgentCat.Internal.Logging;

internal enum LogSeverity
{
    Information,
    Warning,
    Error,
}

internal static class LogSeverityRule
{
    /// <summary>A line that contains fail or error (ASCII letters, any case) is Error, else one that contains "Warning:" is Warning, else Information.</summary>
    internal static LogSeverity Of(string line)
    {
        if (ContainsAsciiIgnoreCase(line, "fail") || ContainsAsciiIgnoreCase(line, "error"))
        {
            return LogSeverity.Error;
        }

        return line.Contains("Warning:") ? LogSeverity.Warning : LogSeverity.Information;
    }

    // JavaScript's /fail|error/i folds the case of ASCII letters only.
    private static bool ContainsAsciiIgnoreCase(string text, string lowerWord)
    {
        for (var start = 0; start + lowerWord.Length <= text.Length; start++)
        {
            var i = 0;
            while (i < lowerWord.Length && ToAsciiLower(text[start + i]) == lowerWord[i])
            {
                i++;
            }

            if (i == lowerWord.Length)
            {
                return true;
            }
        }

        return false;
    }

    private static char ToAsciiLower(char c) => c >= 'A' && c <= 'Z' ? (char)(c + 32) : c;
}
