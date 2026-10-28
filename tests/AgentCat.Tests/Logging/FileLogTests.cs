using System.Diagnostics;
using System.Text.RegularExpressions;
using AgentCat.Internal.Logging;
#if !NET472
using AgentCat.Tests.E2E;
#endif

namespace AgentCat.Tests.Logging;

public sealed class FileLogTests : IDisposable
{
    private static readonly DateTimeOffset Fixed = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "agentcat-filelog-" + Guid.NewGuid().ToString("N"));

    public FileLogTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string TempPath(string name = "agentcat.log") => Path.Combine(_dir, name);

    // TS: targets the home log file on a healthy Node runtime
    [Fact]
    public void Targets_the_home_log_file_on_a_healthy_runtime()
    {
        Assert.Equal("/home/u", LogHome.Resolve(() => "/home/u"));
        Assert.Equal(Path.Combine("/home/u", "agentcat.log"), FileLog.DefaultPath(null, () => "/home/u"));
    }

    // TS: goes silent, never console, when homedir resolves falsy on Node
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Goes_silent_when_the_home_resolves_empty(string? home)
    {
        Assert.Null(LogHome.Resolve(() => home));
    }

    // TS: goes silent, never console, when homedir throws on Node
    [Fact]
    public void Goes_silent_when_the_home_lookup_throws()
    {
        Assert.Null(LogHome.Resolve(() => throw new PlatformNotSupportedException()));
    }

    // TS: goes silent when os.homedir is not implemented at all
    [Fact]
    public void Goes_silent_when_the_home_is_a_relative_path()
    {
        Assert.Null(LogHome.Resolve(() => "relative/home"));
        Assert.Null(LogHome.Resolve(() => "agentcat"));
    }

    [Fact]
    public void Root_slash_is_no_home_for_a_non_root_user()
    {
        Assert.Null(LogHome.Resolve(() => "/"));
        Assert.Equal("/root", LogHome.Resolve(() => "/root"));
    }

    // TS: writes the prefixed entry to the log file
    [Fact]
    public void Writes_the_prefixed_entry_to_the_log_file()
    {
        var path = TempPath();

        new FileLog(path, () => Fixed).Write("Test message");

        Assert.Equal("[2026-10-19T12:00:00.000Z] " + LogLine.Prefix + " Test message\n", File.ReadAllText(path));
    }

    [Fact]
    public void Writes_utf8_without_a_byte_order_mark()
    {
        var path = TempPath();

        new FileLog(path, () => Fixed).Write("café");

        var bytes = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal((byte)'[', bytes[0]);
        Assert.Contains("café", File.ReadAllText(path));
    }

    [Fact]
    public void Missing_home_writes_nothing_in_the_working_directory()
    {
        var before = Directory.GetCurrentDirectory();
        var existed = File.Exists(Path.Combine(before, "agentcat.log"));

        new FileLog(null).Write("one");
        new FileLog(string.Empty).Write("two");
        new FileLog(FileLog.DefaultPath(null, () => string.Empty)).Write("three");

        Assert.Null(FileLog.DefaultPath(null, () => string.Empty));
        Assert.Equal(existed, File.Exists(Path.Combine(before, "agentcat.log")));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Unwritable_home_fails_once()
    {
        var opens = 0;
        var log = new FileLog(
            Path.Combine(_dir, "missing", "agentcat.log"),
            () => Fixed,
            path =>
            {
                opens++;
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            },
            FileLog.DefaultBudget);

        log.Write("one");
        log.Write("two");
        log.Write("three");

        Assert.Equal(1, opens);
        Assert.False(Directory.Exists(Path.Combine(_dir, "missing")));
    }

    [Fact]
    public void A_second_log_on_a_failed_path_performs_no_open()
    {
        var path = Path.Combine(_dir, "missing", "agentcat.log");
        var opens = 0;
        Stream Open(string p)
        {
            opens++;
            return new FileStream(p, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        }

        new FileLog(path, () => Fixed, Open, FileLog.DefaultBudget).Write("one");
        new FileLog(path, () => Fixed, Open, FileLog.DefaultBudget).Write("two");

        Assert.Equal(1, opens);
    }

    [Fact]
    public void A_failed_write_after_a_successful_open_drops_only_that_line()
    {
        var path = TempPath();
        var opens = 0;
        var log = new FileLog(path, () => Fixed, p =>
        {
            opens++;
            var file = new FileStream(p, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            return opens == 1 ? new ThrowingWriteStream(file) : file;
        }, FileLog.DefaultBudget);

        log.Write("lost");
        log.Write("kept");

        Assert.Equal(2, opens);
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("lost", text);
        Assert.Contains(" kept\n", text);
    }

    private sealed class ThrowingWriteStream : Stream
    {
        private readonly Stream _inner;

        internal ThrowingWriteStream(Stream inner) => _inner = inner;

        public override bool CanRead => false;

        public override bool CanSeek => true;

        public override bool CanWrite => true;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("disk full");

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private static Conflict SharingConflict() => new Conflict(Environment.OSVersion.Platform == PlatformID.Win32NT ? unchecked((int)0x80070020) : 11);

    private sealed class Conflict : IOException
    {
        internal Conflict(int hResult)
            : base("held")
        {
            HResult = hResult;
        }
    }

    [Fact]
    public void A_sharing_conflict_that_clears_within_the_budget_still_writes_the_line()
    {
        var path = TempPath();
        var opens = 0;
        var droppedBefore = FileLog.DroppedLines;
        var log = new FileLog(path, () => Fixed, p =>
        {
            opens++;
            return opens <= 5 ? throw SharingConflict() : new FileStream(p, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        }, TimeSpan.FromSeconds(5));

        log.Write("late");

        Assert.Equal(6, opens);
        Assert.Equal(droppedBefore, FileLog.DroppedLines);
        Assert.Contains(" late\n", File.ReadAllText(path));
    }

    // The operating system's own conflict: a stream that holds the file exclusively.
    [Fact]
    public void A_file_held_by_another_stream_drops_the_line_after_the_budget_and_keeps_logging()
    {
        var path = TempPath();
        var droppedBefore = FileLog.DroppedLines;
        var log = new FileLog(path, () => Fixed, null, TimeSpan.FromMilliseconds(50));

        using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
        {
            log.Write("held");
        }

        Assert.True(FileLog.DroppedLines - droppedBefore >= 1);
        log.Write("free");
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("held", text);
        Assert.Contains(" free\n", text);
    }

    [Fact]
    public void A_sharing_conflict_retries_for_the_wall_clock_budget_then_drops_the_line_and_keeps_logging()
    {
        var path = TempPath();
        var opens = 0;
        var held = true;
        var droppedBefore = FileLog.DroppedLines;
        var budget = TimeSpan.FromMilliseconds(100);
        var log = new FileLog(path, () => Fixed, p =>
        {
            opens++;
            return held ? throw SharingConflict() : new FileStream(p, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        }, budget);

        var clock = Stopwatch.StartNew();
        log.Write("held");
        clock.Stop();

        Assert.True(clock.Elapsed >= budget, "The line was dropped before the budget ended: " + clock.Elapsed);
        Assert.True(clock.Elapsed < budget + TimeSpan.FromSeconds(2), "The retries ran past the budget: " + clock.Elapsed);
        Assert.True(opens >= 2);
        Assert.True(FileLog.DroppedLines - droppedBefore >= 1);

        held = false;
        var opensBefore = opens;
        log.Write("free");

        Assert.Equal(opensBefore + 1, opens);
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("held", text);
        Assert.Contains(" free\n", text);
    }

    // TS: should not throw when called
    [Theory]
    [InlineData("Test message")]
    [InlineData("")]
    [InlineData("Special chars: \n\t\r")]
    public void Should_not_throw_when_called(string message)
    {
        var path = TempPath();

        var exception = Record.Exception(() => new FileLog(path).Write(message));

        Assert.Null(exception);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void A_throwing_clock_or_open_does_not_throw()
    {
        Assert.Null(Record.Exception(() => new FileLog(TempPath(), () => throw new InvalidOperationException()).Write("x")));
        Assert.Null(Record.Exception(() => new FileLog(TempPath(), () => Fixed, _ => throw new InvalidOperationException(), FileLog.DefaultBudget).Write("x")));
    }

    // TS: should handle rapid successive calls
    [Fact]
    public void Should_handle_rapid_successive_calls()
    {
        var path = TempPath();
        var log = new FileLog(path);

        for (var i = 0; i < 100; i++)
        {
            log.Write("line " + i);
        }

        var lines = File.ReadAllLines(path);
        Assert.Equal(100, lines.Length);
        for (var i = 0; i < 100; i++)
        {
            Assert.EndsWith(" line " + i, lines[i]);
        }
    }

    [Fact]
    public void Lines_from_many_threads_stay_whole()
    {
        var path = TempPath();
        var log = new FileLog(path);
        var payload = new string('y', 80);

        var threads = Enumerable.Range(0, 8).Select(t => new Thread(() =>
        {
            for (var n = 0; n < 2000; n++)
            {
                log.Write(t + ":" + n + ":" + payload);
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        var shape = new Regex(@"^\[[^\]]+\] \[[^\]]+\] \d+:\d+:y{80}$");
        var lines = File.ReadAllLines(path);
        Assert.Equal(16000, lines.Length);
        Assert.All(lines, line => Assert.Matches(shape, line));
    }

    [Fact]
    public void A_deleted_file_is_created_again()
    {
        var path = TempPath();
        var log = new FileLog(path, () => Fixed);

        log.Write("first");
        File.Delete(path);
        log.Write("second");

        Assert.Equal("[2026-10-19T12:00:00.000Z] " + LogLine.Prefix + " second\n", File.ReadAllText(path));
    }

    [Fact]
    public void The_path_override_wins_over_the_home()
    {
        Assert.Equal("/x/y.log", FileLog.DefaultPath("/x/y.log", () => "/home/u"));
    }

#if !NET472
    [Fact]
    public async Task Two_processes_append_whole_lines()
    {
        var path = TempPath();
        var processes = Enumerable.Range(0, 2).Select(_ => StartWriter(path)).ToList();
        long dropped = 0;
        try
        {
            foreach (var process in processes)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var output = await process.StandardOutput.ReadToEndAsync(cts.Token);
                await process.WaitForExitAsync(cts.Token);
                Assert.Equal(0, process.ExitCode);
                var match = Regex.Match(output.Trim(), @"^dropped=(\d+)$");
                Assert.True(match.Success, "Unexpected writer output: " + output);
                dropped += long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }

                process.Dispose();
            }
        }

        var shape = new Regex(@"^\[[^\]]+\] \[[^\]]+\] \d+:\d+:\d+:x{80}$");
        var lines = File.ReadAllLines(path);
        Assert.All(lines, line => Assert.Matches(shape, line));
        // A dropped line (retries exhausted under contention) is allowed; a lost line (an overwrite) is not.
        Assert.Equal(40000, lines.Length + dropped);
        Assert.True(dropped <= 400, "Dropped " + dropped + " of 40000 lines.");
    }

    private static Process StartWriter(string path)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        startInfo.ArgumentList.Add(Lanes.ServerDll);
        startInfo.Environment["AGENTCAT_E2E_MODE"] = "log-writer";
        startInfo.Environment["AGENTCAT_E2E_LINES"] = "5000";
        startInfo.Environment["AGENTCAT_E2E_THREADS"] = "4";
        startInfo.Environment["AGENTCAT_TEST_LOG_PATH"] = path;
        startInfo.Environment["AGENTCAT_DISABLE_DIAGNOSTICS"] = "true";
        return Process.Start(startInfo)!;
    }
#endif
}
