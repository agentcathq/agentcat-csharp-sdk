namespace AgentCat.Tests.TestSupport;

internal static class Retention
{
    // Collects until every reference is dead, for at most WaitBound, and returns the labels still alive. A reference
    // can stay reachable for a moment from the frames of a thread that has not finished returning, and nothing signals
    // when it has:
    // - The MCP SDK handles every incoming message on a task that McpSessionHandler.ProcessMessagesCoreAsync starts and
    //   drops (its local function ProcessMessageAsync). On ModelContextProtocol 1.0.0 neither McpServer.RunAsync nor
    //   McpServer.DisposeAsync waits for that task; 1.1.0 and later wait for it in RunAsync. Its frames reach the
    //   session's McpSessionHandler and, through its JsonRpcMessageFilter, the McpServer and its options, so the handler
    //   of the client's notifications/initialized keeps a disposed server reachable until its thread gets a CPU again.
    // - A thread that resumes a test's helper runs the rest of it inline, and at its end the test's continuation. After
    //   Task.Yield another thread can collect while such a thread still returns through frames that reference the
    //   helper's locals, a harness and its server.
    // A reference that something keeps for good still fails the caller's check, once WaitBound has passed.
    internal static async Task<string[]> LiveAfterCollectingAsync(IReadOnlyList<(string Label, WeakReference Reference)> references)
    {
        var deadline = DateTime.UtcNow + FakeAgentCatApi.WaitBound;
        while (true)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var live = references.Where(r => r.Reference.IsAlive).Select(r => r.Label).ToArray();
            if (live.Length == 0 || DateTime.UtcNow >= deadline)
            {
                return live;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
