#pragma warning disable CA1861, CA1305, CA1872, CA1850, CA1837, CA2016

using AgentCat.Internal.Diagnostics;
using AgentCat.Internal.Runtime;

namespace AgentCat.Tests.Diagnostics;

public class DiagnosticsOptOutTests
{
    private static DiagnosticsHarness Outside() => new(inTestRun: false);

    // TS: is enabled by default outside test environments
    [Fact]
    public void Is_enabled_by_default_outside_test_environments()
    {
        var h = Outside().Started();

        Assert.True(h.Diagnostics.IsEnabled);
        Assert.NotNull(h.Sink);
    }

    // TS: is disabled via the option
    [Fact]
    public void Is_disabled_via_the_option()
    {
        var h = Outside().Started(disabled: true);

        Assert.False(h.Diagnostics.IsEnabled);
        Assert.Null(h.Sink);
    }

    // TS: is disabled via the env var (DISABLE_DIAGNOSTICS=1: C# keeps the unprefixed name as an off switch)
    [Fact]
    public void Is_disabled_via_the_env_var()
    {
        var h = Outside().Set("DISABLE_DIAGNOSTICS", "1").Started();

        Assert.False(h.Diagnostics.IsEnabled);
    }

    // TS: is disabled when DISABLE_DIAGNOSTICS=%s (the unprefixed name)
    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("on")]
    public void DISABLE_DIAGNOSTICS_true_words_disable_diagnostics(string value)
    {
        var h = Outside().Set("DISABLE_DIAGNOSTICS", value).Started();

        Assert.False(h.Diagnostics.IsEnabled);
    }

    // TS: is disabled when DISABLE_DIAGNOSTICS=%s (the prefixed name, which C# adds)
    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("on")]
    public void AGENTCAT_DISABLE_DIAGNOSTICS_true_words_disable_diagnostics(string value)
    {
        var h = Outside().Set("AGENTCAT_DISABLE_DIAGNOSTICS", value).Started();

        Assert.False(h.Diagnostics.IsEnabled);
    }

    // TS: stays enabled when DISABLE_DIAGNOSTICS=%s. C# does the opposite for the unprefixed name: its false words
    // are ignored, so in a test run diagnostics stay off.
    [Theory]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("0")]
    [InlineData("no")]
    [InlineData("off")]
    public void Unprefixed_DISABLE_DIAGNOSTICS_false_does_not_force_enable(string value)
    {
        var h = new DiagnosticsHarness(inTestRun: true).Set("DISABLE_DIAGNOSTICS", value).Started();

        Assert.False(h.Diagnostics.IsEnabled);
    }

    // The prefixed name's false words force-enable diagnostics under a test runner.
    [Theory]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("0")]
    [InlineData("no")]
    [InlineData("off")]
    public void AGENTCAT_DISABLE_DIAGNOSTICS_false_words_force_enable_diagnostics_in_a_test_run(string value)
    {
        var h = new DiagnosticsHarness(inTestRun: true).Set("AGENTCAT_DISABLE_DIAGNOSTICS", value).Started();

        Assert.True(h.Diagnostics.IsEnabled);
    }

    [Theory]
    [InlineData("true", "false")]
    [InlineData("false", "true")]
    [InlineData("1", "off")]
    [InlineData("0", "yes")]
    public void Either_variable_saying_off_wins(string prefixed, string unprefixed)
    {
        var h = new DiagnosticsHarness(inTestRun: true)
            .Set("AGENTCAT_DISABLE_DIAGNOSTICS", prefixed)
            .Set("DISABLE_DIAGNOSTICS", unprefixed)
            .Started();

        Assert.False(h.Diagnostics.IsEnabled);
    }

    // The option wins over a force-enable.
    [Fact]
    public void The_option_beats_a_force_enable()
    {
        var h = new DiagnosticsHarness(inTestRun: true).ForceEnabled().Started(disabled: true);

        Assert.False(h.Diagnostics.IsEnabled);
    }

    // TS: treats a whitespace-only value as unset (default behavior applies)
    [Fact]
    public void Treats_a_whitespace_only_value_as_unset()
    {
        var outside = Outside().Set("DISABLE_DIAGNOSTICS", "  ").Set("AGENTCAT_DISABLE_DIAGNOSTICS", " \t").Started();
        var inside = new DiagnosticsHarness(inTestRun: true).Set("AGENTCAT_DISABLE_DIAGNOSTICS", "  ").Started();

        Assert.True(outside.Diagnostics.IsEnabled);
        Assert.False(inside.Diagnostics.IsEnabled);
    }

    // TS: is disabled by default when %s=%s (test environment). The .NET test signals come from TestEnvironment.
    [Theory]
    [InlineData("testhost", "", "")]
    [InlineData("App", "xunit.v3.core", "")]
    [InlineData("App", "nunit.framework", "")]
    public void Is_disabled_by_default_in_a_test_environment(string entry, string loaded, string platformAssemblies)
    {
        var inTestRun = TestEnvironment.Detect(entry, new string?[] { loaded }, platformAssemblies.Length == 0 ? null : platformAssemblies);
        var h = new DiagnosticsHarness(inTestRun).Started();

        Assert.True(inTestRun);
        Assert.False(h.Diagnostics.IsEnabled);
    }

    // TS: track() without options does not enable diagnostics under vitest. This process is a test run.
    [Fact]
    public async Task Track_without_options_does_not_enable_diagnostics_in_a_test_run()
    {
        var h = new DiagnosticsHarness(TestEnvironment.IsTestRun()).Started();
        h.Write("Warning: nothing");

        await h.FlushAsync();

        Assert.True(TestEnvironment.IsTestRun());
        Assert.False(h.Diagnostics.IsEnabled);
        Assert.Empty(h.Handler.Requests);
    }

    // TS: DISABLE_DIAGNOSTICS=false force-enables diagnostics even under vitest. C# reads the prefixed name:
    // AGENTCAT_DISABLE_DIAGNOSTICS=false.
    [Fact]
    public void AGENTCAT_DISABLE_DIAGNOSTICS_false_force_enables_diagnostics_in_a_test_run()
    {
        var h = new DiagnosticsHarness(TestEnvironment.IsTestRun()).ForceEnabled().Started("proj_test_env");

        Assert.True(h.Diagnostics.IsEnabled);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("on")]
    public void AGENTCAT_ENABLED_true_does_not_enable_diagnostics_in_a_test_run(string value)
    {
        var h = new DiagnosticsHarness(inTestRun: true).Set("AGENTCAT_ENABLED", value).Started();

        Assert.False(h.Diagnostics.IsEnabled);
        Assert.False(DiagnosticsSwitch.IsEnabled(false, name => name == "AGENTCAT_ENABLED" ? value : null, inTestRun: true));
    }

    [Fact]
    public void A_variable_that_throws_counts_as_unset()
    {
        Assert.True(DiagnosticsSwitch.IsEnabled(false, _ => throw new InvalidOperationException("boom"), inTestRun: false));
        Assert.False(DiagnosticsSwitch.IsEnabled(false, _ => throw new InvalidOperationException("boom"), inTestRun: true));
    }

    [Fact]
    public void First_install_decides()
    {
        var off = Outside().Started("proj_a", disabled: true);
        off.Diagnostics.Init("proj_b", false);

        var on = Outside().Started("proj_a");
        on.Diagnostics.Init("proj_b", true);

        Assert.False(off.Diagnostics.IsEnabled);
        Assert.Null(off.Sink);
        Assert.True(on.Diagnostics.IsEnabled);
        Assert.Equal("proj_a", on.Diagnostics.Resource.Single(a => a.Key == "agentcat.project_id").Value);
    }

    [Fact]
    public void The_inert_instance_never_starts()
    {
        var inert = AgentCat.Internal.Diagnostics.Diagnostics.Inert;

        inert.Init("proj_1", false);

        Assert.False(inert.IsEnabled);
    }
}
