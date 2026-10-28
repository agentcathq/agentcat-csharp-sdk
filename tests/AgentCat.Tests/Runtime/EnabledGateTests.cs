using AgentCat.Internal.Runtime;
using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.Runtime;

// The unknown-value warning is once per process, so these tests run alone.
[Collection(SharedRuntime.Name)]
public sealed class EnabledGateTests : IDisposable
{

    public EnabledGateTests() => EnabledGate.ResetUnknownValueWarning();

    public void Dispose()
    {
        EnabledGate.ResetUnknownValueWarning();
        EnabledGate.ResetSkippedAnInstall();
    }

    public static TheoryData<string> TrueWords => new() { "true", "1", "yes", "on", " TRUE ", "Yes", "oN", "\t1\n" };

    public static TheoryData<string> FalseWords => new() { "false", "0", "no", "off", " FALSE ", "No", "oFF", "\t0\n" };

    public static TheoryData<string?> UnsetValues => new() { (string?)null, "", " ", "\t\r\n" };

    private static string UnknownValue(string value)
        => "WARN: AGENTCAT_ENABLED \"" + value + "\" is not one of true, 1, yes, on, false, 0, no, off; the default is used";

    private static Func<string, string?> Variable(string? value)
        => name => name == "AGENTCAT_ENABLED" ? value : null;

    [Theory]
    [MemberData(nameof(TrueWords))]
    public void A_true_word_turns_AgentCat_on(string value)
    {
        var log = new RecordingLog();

        Assert.True(new EnabledGate(Variable(value), () => false, log).IsEnabled());
        Assert.Empty(log.Lines);
    }

    [Theory]
    [MemberData(nameof(FalseWords))]
    public void A_false_word_turns_AgentCat_off_outside_test_runs_too(string value)
    {
        var log = new RecordingLog();

        Assert.False(new EnabledGate(Variable(value), () => false, log).IsEnabled());
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void True_wins_over_a_test_run()
    {
        var asked = 0;
        var gate = new EnabledGate(Variable("true"), () => ++asked > 0, new RecordingLog());

        Assert.True(gate.IsEnabled());
        Assert.Equal(0, asked);
    }

    [Fact]
    public void False_wins_over_a_test_run()
    {
        var asked = 0;
        var gate = new EnabledGate(Variable("false"), () => ++asked > 0, new RecordingLog());

        Assert.False(gate.IsEnabled());
        Assert.Equal(0, asked);
    }

    [Theory]
    [MemberData(nameof(UnsetValues))]
    public void Unset_or_whitespace_is_off_in_a_test_run_and_on_elsewhere(string? value)
    {
        var log = new RecordingLog();

        Assert.False(new EnabledGate(Variable(value), () => true, log).IsEnabled());
        Assert.True(new EnabledGate(Variable(value), () => false, log).IsEnabled());
        Assert.Empty(log.Lines);
    }

    [Theory]
    [InlineData("enabled")]
    [InlineData("flase")]
    [InlineData("2")]
    [InlineData(" Maybe ")]
    public void An_unknown_value_logs_its_line_once_and_uses_the_default(string value)
    {
        var log = new RecordingLog();
        var otherLog = new RecordingLog();

        Assert.True(new EnabledGate(Variable(value), () => false, log).IsEnabled());
        Assert.False(new EnabledGate(Variable(value), () => true, log).IsEnabled());
        Assert.False(new EnabledGate(Variable(value), () => true, otherLog).IsEnabled());

        Assert.Equal(UnknownValue(value), Assert.Single(log.Lines));
        Assert.Empty(otherLog.Lines);
    }

    [Fact]
    public void The_variable_is_read_on_every_call()
    {
        string? value = "false";
        var gate = new EnabledGate(name => name == "AGENTCAT_ENABLED" ? value : null, () => true, new RecordingLog());

        Assert.False(gate.IsEnabled());
        value = "on";
        Assert.True(gate.IsEnabled());
        value = null;
        Assert.False(gate.IsEnabled());
    }

    // An install that passed on the detection proves the process is no test run: later checks, a custom event's
    // among them, skip the scan of the loaded assemblies. A set variable still wins on every call.
    [Fact]
    public void A_production_install_caches_the_detection_and_a_set_variable_still_wins()
    {
        string? value = null;
        var detections = 0;
        var gate = new EnabledGate(
            name => name == "AGENTCAT_ENABLED" ? value : null,
            () =>
            {
                detections++;
                return false;
            },
            new RecordingLog());

        Assert.True(gate.IsEnabled());
        Assert.True(gate.IsEnabled());
        Assert.Equal(2, detections);

        Assert.True(gate.AllowsInstall());
        Assert.Equal(3, detections);

        Assert.True(gate.IsEnabled());
        Assert.True(gate.AllowsInstall());
        Assert.Equal(3, detections);

        value = "off";
        Assert.False(gate.IsEnabled());
        Assert.False(gate.AllowsInstall());
        value = "yes";
        Assert.True(gate.IsEnabled());
        value = " ";
        Assert.True(gate.IsEnabled());
        Assert.Equal(3, detections);
    }

    // Only the detection's own answer is kept: an install that the variable let through, or a test run, proves nothing.
    [Fact]
    public void An_install_decided_by_the_variable_or_in_a_test_run_caches_nothing()
    {
        string? value = "true";
        var testRun = false;
        var detections = 0;
        var gate = new EnabledGate(
            name => name == "AGENTCAT_ENABLED" ? value : null,
            () =>
            {
                detections++;
                return testRun;
            },
            new RecordingLog());

        Assert.True(gate.AllowsInstall());
        value = null;
        Assert.True(gate.IsEnabled());
        Assert.True(gate.IsEnabled());
        Assert.Equal(2, detections);

        testRun = true;
        Assert.False(gate.AllowsInstall());
        testRun = false;
        Assert.True(gate.IsEnabled());
        Assert.Equal(4, detections);
    }

    // The live inputs of the shared runtime: this process loads xUnit, so without the variable AgentCat is off.
    [Fact]
    public void The_shared_runtime_is_off_in_this_test_run_unless_the_variable_turns_it_on()
    {
        var previous = Environment.GetEnvironmentVariable(EnabledGate.Variable);
        try
        {
            Environment.SetEnvironmentVariable(EnabledGate.Variable, "");
            Assert.False(AgentCatRuntime.Shared.Gate.IsEnabled());

            Environment.SetEnvironmentVariable(EnabledGate.Variable, "true");
            Assert.True(AgentCatRuntime.Shared.Gate.IsEnabled());
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnabledGate.Variable, previous);
        }
    }

    [Fact]
    public void Failing_inputs_never_throw_and_a_failing_test_check_counts_as_no_test_run()
    {
        var log = new RecordingLog();

        Assert.False(new EnabledGate(_ => throw new InvalidOperationException("env boom"), () => true, log).IsEnabled());
        Assert.True(new EnabledGate(_ => throw new InvalidOperationException("env boom"), () => false, log).IsEnabled());
        Assert.True(new EnabledGate(Variable(null), () => throw new InvalidOperationException("scan boom"), log).IsEnabled());

        log.ThrowWhen = _ => true;
        Assert.True(new EnabledGate(Variable("unknown"), () => false, log).IsEnabled());
        Assert.Empty(log.Lines);
    }
}
