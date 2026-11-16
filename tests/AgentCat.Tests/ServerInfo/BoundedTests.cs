using AgentCat.Internal.ServerInfo;

namespace AgentCat.Tests.ServerInfo;

public class BoundedTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Work_that_ends_in_time_gives_its_result()
    {
        Assert.Equal(7, await Bounded.RunAsync(_ => Task.FromResult(7), Long, "late", CancellationToken.None));
        Assert.Equal(8, await Bounded.RunAsync(
            async _ =>
            {
                await Task.Yield();
                return 8;
            },
            Long,
            "late",
            CancellationToken.None));
    }

    [Fact]
    public async Task Work_that_fails_in_time_gives_its_error()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Bounded.RunAsync<int>(
            async _ =>
            {
                await Task.Yield();
                throw new InvalidOperationException("broken");
            },
            Long,
            "late",
            CancellationToken.None));

        Assert.Equal("broken", error.Message);
    }

    [Fact]
    public async Task Work_that_passes_the_limit_is_cancelled_and_left_alone()
    {
        var never = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = CancellationToken.None;

        var error = await Assert.ThrowsAsync<HeartbeatFailure>(() => Bounded.RunAsync(
            given =>
            {
                token = given;
                return never.Task;
            },
            TimeSpan.FromMilliseconds(20),
            "too late",
            CancellationToken.None));

        Assert.Equal("too late", error.Message);
        Assert.True(token.IsCancellationRequested);

        // The error of work that ends after its limit is observed and dropped.
        never.SetException(new InvalidOperationException("after the limit"));
    }

    [Fact]
    public async Task No_time_left_starts_no_work()
    {
        var started = false;

        await Assert.ThrowsAsync<HeartbeatFailure>(() => Bounded.RunAsync(
            _ =>
            {
                started = true;
                return Task.FromResult(1);
            },
            TimeSpan.Zero,
            "no time",
            CancellationToken.None));

        Assert.False(started);
    }

    [Fact]
    public async Task A_cancelled_caller_cancels_the_token_of_the_work()
    {
        using var caller = new CancellationTokenSource();
        var seen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = Bounded.RunAsync(
            async given =>
            {
                given.Register(() => seen.TrySetResult(true));
                await Task.Delay(Timeout.Infinite, given);
                return 1;
            },
            Long,
            "late",
            caller.Token);
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(await seen.Task);
    }

    [Fact]
    public async Task Work_that_cannot_start_gives_its_error()
        => await Assert.ThrowsAsync<InvalidOperationException>(() => Bounded.RunAsync<int>(
            _ => throw new InvalidOperationException("no start"),
            Long,
            "late",
            CancellationToken.None));
}
