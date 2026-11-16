using AgentCat.Internal.ServerInfo;

namespace AgentCat.Tests.ServerInfo;

public class HeartbeatGuardTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void The_first_check_of_a_key_begins_and_the_next_waits_3600_seconds()
    {
        var guard = new HeartbeatGuard();

        Assert.True(guard.TryBegin("proj", "server", needsSettledMemory: false, Start));
        Assert.False(guard.TryBegin("proj", "server", needsSettledMemory: false, Start));
        Assert.False(guard.TryBegin("proj", "server", needsSettledMemory: false, Start.AddSeconds(3599.999)));
        Assert.True(guard.TryBegin("proj", "server", needsSettledMemory: false, Start.AddSeconds(3600)));
        Assert.False(guard.TryBegin("proj", "server", needsSettledMemory: false, Start.AddSeconds(7199)));
        Assert.Equal(2, guard.Begun);
    }

    [Fact]
    public void Each_project_and_each_server_name_has_its_own_attempt()
    {
        var guard = new HeartbeatGuard();

        Assert.True(guard.TryBegin("proj_a", "server", needsSettledMemory: false, Start));
        Assert.True(guard.TryBegin("proj_b", "server", needsSettledMemory: false, Start));
        Assert.True(guard.TryBegin("proj_a", "other", needsSettledMemory: false, Start));
        Assert.False(guard.TryBegin("proj_a", "server", needsSettledMemory: false, Start));
        Assert.Equal(3, guard.Count);
    }

    [Fact]
    public void A_check_that_needs_a_settled_memory_waits_60_seconds_from_the_first_check()
    {
        var guard = new HeartbeatGuard();

        Assert.False(guard.TryBegin("proj", "server", needsSettledMemory: true, Start));
        Assert.False(guard.TryBegin("proj", "server", needsSettledMemory: true, Start.AddSeconds(59.999)));
        Assert.Equal(0, guard.Count);
        Assert.True(guard.TryBegin("proj", "server", needsSettledMemory: true, Start.AddSeconds(60)));
    }

    [Fact]
    public void A_cleared_memory_starts_again_as_a_new_process_does()
    {
        var guard = new HeartbeatGuard();
        Assert.True(guard.TryBegin("proj", "server", needsSettledMemory: false, Start));

        guard.Clear();

        Assert.False(guard.TryBegin("proj", "server", needsSettledMemory: true, Start.AddSeconds(100)));
        Assert.True(guard.TryBegin("proj", "server", needsSettledMemory: false, Start.AddSeconds(100)));
    }

    [Fact]
    public void A_full_memory_is_emptied_and_never_holds_more_than_1000_keys()
    {
        var guard = new HeartbeatGuard();
        for (var i = 0; i < HeartbeatGuard.MaxKeys; i++)
        {
            Assert.True(guard.TryBegin("proj", "server-" + i, needsSettledMemory: false, Start));
        }

        Assert.Equal(1000, guard.Count);
        Assert.False(guard.TryBegin("proj", "server-0", needsSettledMemory: false, Start));
        Assert.Equal(1000, guard.Count);

        Assert.True(guard.TryBegin("proj", "one-more", needsSettledMemory: false, Start));
        Assert.Equal(1, guard.Count);
        Assert.True(guard.TryBegin("proj", "server-0", needsSettledMemory: false, Start));

        // The age of the memory stays: a tool call still counts as one in a settled memory.
        Assert.True(guard.TryBegin("proj", "settled", needsSettledMemory: true, Start.AddSeconds(60)));
    }

    [Fact]
    public void A_clock_that_went_back_lets_a_heartbeat_begin_and_restarts_the_age()
    {
        var guard = new HeartbeatGuard();
        Assert.True(guard.TryBegin("proj", "server", needsSettledMemory: false, Start));

        Assert.True(guard.TryBegin("proj", "server", needsSettledMemory: false, Start.AddHours(-5)));
        Assert.False(guard.TryBegin("proj", "server", needsSettledMemory: false, Start.AddHours(-5).AddSeconds(10)));
        Assert.False(guard.TryBegin("proj", "other", needsSettledMemory: true, Start.AddHours(-5).AddSeconds(59)));
        Assert.True(guard.TryBegin("proj", "other", needsSettledMemory: true, Start.AddHours(-5).AddSeconds(60)));
    }

    [Fact]
    public async Task Of_many_checks_at_the_same_moment_one_begins()
    {
        var guard = new HeartbeatGuard();
        using var go = new ManualResetEventSlim();
        var threads = Enumerable.Range(0, 16).Select(_ => new TaskCompletionSource<bool>()).ToArray();
        for (var i = 0; i < threads.Length; i++)
        {
            var done = threads[i];
            new Thread(() =>
            {
                go.Wait();
                done.SetResult(guard.TryBegin("proj", "server", needsSettledMemory: false, Start));
            }).Start();
        }

        go.Set();
        var results = await Task.WhenAll(threads.Select(t => t.Task));

        Assert.Single(results, began => began);
        Assert.Equal(1, guard.Begun);
    }
}
