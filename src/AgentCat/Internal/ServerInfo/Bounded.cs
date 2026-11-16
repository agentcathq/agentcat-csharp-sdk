using System;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCat.Internal.ServerInfo;

internal static class Bounded
{
    // Runs the work with a time limit. Work that passes the limit gets its token cancelled and is left to finish
    // alone: its outcome is observed and dropped.
    internal static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> start,
        TimeSpan limit,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        if (limit <= TimeSpan.Zero)
        {
            throw new TimeoutException(timeoutMessage);
        }

        var abandon = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var handedOff = false;
        try
        {
            var work = start(abandon.Token);
            if (!work.IsCompleted)
            {
                bool timedOut;
                using (var timer = new CancellationTokenSource())
                {
                    timedOut = await Task.WhenAny(work, Task.Delay(limit, timer.Token)).ConfigureAwait(false) != work;
                    timer.Cancel();
                }

                if (timedOut)
                {
                    handedOff = true;
                    _ = AbandonAsync(work, abandon);
                    throw new TimeoutException(timeoutMessage);
                }
            }

            return await work.ConfigureAwait(false);
        }
        finally
        {
            if (!handedOff)
            {
                abandon.Dispose();
            }
        }
    }

    private static async Task AbandonAsync(Task work, CancellationTokenSource abandon)
    {
        try
        {
            abandon.Cancel();
        }
        catch (Exception)
        {
        }

        try
        {
            await work.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
        finally
        {
            abandon.Dispose();
        }
    }
}
