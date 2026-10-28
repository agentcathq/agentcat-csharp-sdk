using System;

namespace AgentCat.Internal.Errors;

// Exception.ToString writes the header, then the InnerException's text closed by a line of its own, then the
// exception's own frames; AggregateException then appends one section per inner exception, each ending in "<---"
// and a newline (.NET leaves out the ones that are the InnerException, .NET Framework writes them all). The closing
// line is localized on .NET Framework, so it is read from the runtime once.
internal static class ExceptionLayout
{
    private const string SectionEnd = "<---";

    private static readonly string InnerClosingLine = ReadInnerClosingLine();

    // The start and end of the exception's own frame lines in its ToString text, or null when the text does not
    // follow the layout.
    internal static (int Start, int End)? OwnFrames(string text, Exception exception)
    {
        try
        {
            var end = text.Length;
            if (exception is AggregateException aggregate)
            {
                var inners = aggregate.InnerExceptions;
                for (var i = inners.Count - 1; i >= 0; i--)
                {
                    var section = ExceptionCapture.StableStack(inners[i].ToString()) + SectionEnd + Environment.NewLine;
                    var start = end - section.Length;
                    if (start > 0 && string.CompareOrdinal(text, start, section, 0, section.Length) == 0)
                    {
                        var newline = text.LastIndexOf('\n', start - 1);
                        if (newline < 0)
                        {
                            return null;
                        }

                        end = newline > 0 && text[newline - 1] == '\r' ? newline - 1 : newline;
                    }
                    else if (!ReferenceEquals(inners[i], exception.InnerException))
                    {
                        return null;
                    }
                }
            }

            if (exception.InnerException is null)
            {
                var header = HeaderOf(exception);
                return (text.StartsWith(header, StringComparison.Ordinal) ? header.Length : 0, end);
            }

            var closing = ClosingLineBefore(text, end);
            return closing < 0 ? null : (closing, end);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string HeaderOf(Exception exception)
    {
        var message = exception.Message;
        var name = exception.GetType().ToString();
        return string.IsNullOrEmpty(message) ? name : name + ": " + message;
    }

    // Returns the end of the last line before the given position that is the InnerException's closing line.
    private static int ClosingLineBefore(string text, int end)
    {
        var lineEnd = end;
        while (lineEnd > 0)
        {
            var newline = text.LastIndexOf('\n', lineEnd - 1);
            var lineStart = newline + 1;
            var length = lineEnd - lineStart;
            if (length > 0 && text[lineEnd - 1] == '\r')
            {
                length--;
            }

            if (length == InnerClosingLine.Length && string.CompareOrdinal(text, lineStart, InnerClosingLine, 0, length) == 0)
            {
                return lineStart + length;
            }

            if (newline < 0)
            {
                break;
            }

            lineEnd = newline;
        }

        return -1;
    }

    private static string ReadInnerClosingLine()
    {
        var text = new InvalidOperationException(string.Empty, new InvalidOperationException(string.Empty)).ToString();
        return text.Substring(text.LastIndexOf('\n') + 1).TrimEnd('\r');
    }
}
