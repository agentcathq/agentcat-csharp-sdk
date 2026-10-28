using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AgentCat.Internal.Logging;

/// <summary>
/// Appends log lines to a file: one exclusive open per line under a process-wide lock, never a kept-open stream.
/// </summary>
internal sealed class FileLog : IAgentCatLog
{
    internal const string FileName = "agentcat.log";

    // How long one line retries a sharing conflict. The wait between attempts is Thread.Sleep(1), which lasts about
    // 15 ms on Windows, so the budget is wall-clock time and not a count of attempts.
    internal static readonly TimeSpan DefaultBudget = TimeSpan.FromMilliseconds(50);

    // Linux and macOS report a held flock as EWOULDBLOCK (11 and 35). Windows reports ERROR_SHARING_VIOLATION (32)
    // and ERROR_LOCK_VIOLATION (33) in the low 16 bits.
    private static readonly int[] UnixConflictCodes = { 11, 35 };

    private static readonly object Gate = new();

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    // Paths whose open failed for a reason other than a sharing conflict: off for the rest of the process.
    private static readonly HashSet<string> FailedPaths = new(StringComparer.Ordinal);

    private readonly string? _path;
    private readonly string? _key;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<string, Stream> _open;
    private readonly TimeSpan _budget;

    internal FileLog(string? path, Func<DateTimeOffset>? clock = null)
        : this(path, clock, null, DefaultBudget)
    {
    }

    internal FileLog(string? path, Func<DateTimeOffset>? clock, Func<string, Stream>? open, TimeSpan budget)
    {
        _budget = budget;
        _path = string.IsNullOrEmpty(path) ? null : path;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _open = open ?? OpenExclusive;
        _key = _path is null ? null : FullPath(_path);
    }

    /// <summary>Lines that a write gave up on after the path resolved: retries exhausted, a failed open or a failed write.</summary>
    internal static long DroppedLines => Interlocked.Read(ref _droppedLines);

    private static long _droppedLines;

    /// <summary>Test seam: the path <c>DefaultPath</c> returns instead of the home file.</summary>
    internal static string? PathOverride { get; set; }

    internal static string? DefaultPath()
        => DefaultPath(PathOverride, () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static string? DefaultPath(string? pathOverride, Func<string?> userProfile)
    {
        if (!string.IsNullOrEmpty(pathOverride))
        {
            return pathOverride;
        }

        var home = LogHome.Resolve(userProfile);
        return home is null ? null : Path.Combine(home, FileName);
    }

    public void Write(string message) => WriteLine(Format(message));

    public void Write(string message, LogSeverity hostSeverity) => Write(message);

    /// <summary>Formats one line with this log's clock.</summary>
    internal string Format(string message)
    {
        try
        {
            return LogLine.Format(_clock(), message);
        }
        catch (Exception)
        {
            return LogLine.Format(DateTimeOffset.UtcNow, message);
        }
    }

    /// <summary>Appends an already formatted line, without its line break.</summary>
    internal void WriteLine(string line)
    {
        try
        {
            var path = _path;
            if (path is null)
            {
                return;
            }

            var bytes = Utf8.GetBytes(line + "\n");
            lock (Gate)
            {
                if (FailedPaths.Contains(_key!))
                {
                    return;
                }

                var elapsed = Stopwatch.StartNew();
                while (true)
                {
                    Stream stream;
                    try
                    {
                        stream = _open(path);
                    }
                    catch (IOException e) when (IsSharingConflict(e))
                    {
                        if (elapsed.Elapsed >= _budget)
                        {
                            Interlocked.Increment(ref _droppedLines);
                            return;
                        }

                        Thread.Sleep(1);
                        continue;
                    }
                    catch (Exception)
                    {
                        FailedPaths.Add(_key!);
                        Interlocked.Increment(ref _droppedLines);
                        return;
                    }

                    try
                    {
                        using (stream)
                        {
                            stream.Seek(0, SeekOrigin.End);
                            stream.Write(bytes, 0, bytes.Length);
                            stream.Flush();
                        }
                    }
                    catch (Exception)
                    {
                        // The file opened: this line is dropped, the file stays on.
                        Interlocked.Increment(ref _droppedLines);
                    }

                    return;
                }
            }
        }
        catch (Exception)
        {
            // A log line never breaks the caller.
        }
    }

    private static string FullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return path;
        }
    }

    private static FileStream OpenExclusive(string path)
    {
        var share = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? FileShare.Read : FileShare.None;
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, share, 1, FileOptions.None);
    }

    private static bool IsSharingConflict(IOException e)
    {
        if (e is FileNotFoundException or DirectoryNotFoundException or PathTooLongException or DriveNotFoundException)
        {
            return false;
        }

        var code = e.HResult;
        if (Array.IndexOf(UnixConflictCodes, code) >= 0)
        {
            return true;
        }

        var low = code & 0xFFFF;
        return (code & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) && (low == 32 || low == 33);
    }
}
