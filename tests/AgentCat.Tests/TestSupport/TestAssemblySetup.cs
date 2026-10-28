#if NET
using System.Runtime.CompilerServices;
#endif
using AgentCat.Internal.Lifecycle;
using AgentCat.Internal.Logging;
#if NETFRAMEWORK
using Xunit.Sdk;
using Xunit.v3;

[assembly: TestPipelineStartup(typeof(AgentCat.Tests.TestSupport.NetFrameworkTestSetup))]
#endif

namespace AgentCat.Tests.TestSupport;

internal static class TestAssemblySetup
{
#if NET
    [ModuleInitializer]
#endif
    internal static void Init()
    {
#if NETFRAMEWORK
        var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
#else
        var pid = Environment.ProcessId;
#endif
        var dir = Path.Combine(Path.GetTempPath(), "agentcat-tests-" + pid);
        Directory.CreateDirectory(dir);
        FileLog.PathOverride = Path.Combine(dir, "agentcat.log");

        // The folder belongs to this process only. A line written after the delete fails to open and turns the file
        // off, which is harmless at exit.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        };
        Environment.SetEnvironmentVariable("AGENTCAT_DISABLE_DIAGNOSTICS", "true");
        Environment.SetEnvironmentVariable("AGENTCAT_ENABLED", "true");

        // A real process-exit or signal handler would drain the shared queue when the test run ends or is interrupted.
        ShutdownHandlers.Hooks = new RecordingShutdownHooks();

        // Tests block thread-pool threads on purpose: synchronous process-exit drains, hooks and handlers held by a
        // gate. With the default minimum of one thread per core the pool adds threads slowly, and queued work, such as
        // a fake API's answer, waited up to 7 s on the 4-core Windows runner: longer than the 5 s stop window that the
        // stdio shutdown tests rely on.
        ThreadPool.GetMinThreads(out var workers, out var completionPorts);
        ThreadPool.SetMinThreads(Math.Max(workers, 64), completionPorts);
    }
}

#if NETFRAMEWORK
// On .NET Framework a module initializer runs before AppContext knows the app's target framework, and the first path
// call there fixes the AppContext defaults of a .NET Framework 4.0 app for the whole process: every compatibility
// switch keeps the old behavior, Switch.System.Diagnostics.IgnorePortablePDBsInStackTraces among them, which leaves
// file names and line numbers out of every stack trace. The xUnit pipeline starts later, and still before any test is
// discovered.
internal sealed class NetFrameworkTestSetup : ITestPipelineStartup
{
    public ValueTask StartAsync(IMessageSink diagnosticMessageSink)
    {
        TestAssemblySetup.Init();
        return default;
    }

    public ValueTask StopAsync() => default;
}
#endif
