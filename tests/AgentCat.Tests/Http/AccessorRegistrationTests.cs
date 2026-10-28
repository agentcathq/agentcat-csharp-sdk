#if NET
using AgentCat.Internal.Engine;
using AgentCat.Internal.Http;
using AgentCat.Internal.Runtime;
using AgentCat.Tests.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace AgentCat.Tests.Http;

// An install that the gate stops is remembered for the process, so these tests run alone.
[Collection(SharedRuntime.Name)]
public sealed class AccessorRegistrationTests : IDisposable
{
    public AccessorRegistrationTests() => EnabledGate.ResetSkippedAnInstall();

    public void Dispose() => EnabledGate.ResetSkippedAnInstall();

    [Fact]
    public void Registration_is_idempotent_before_and_after_AddHttpContextAccessor()
    {
        var appFirst = new ServiceCollection();
        appFirst.AddHttpContextAccessor();
        AspNetCoreRequest.TryRegisterAccessor(appFirst);

        var agentCatOnly = new ServiceCollection();
        AspNetCoreRequest.TryRegisterAccessor(agentCatOnly);
        AspNetCoreRequest.TryRegisterAccessor(agentCatOnly);
        AssertOneAccessor(agentCatOnly);
        agentCatOnly.AddHttpContextAccessor();

        var withAgentCatOnly = new ServiceCollection();
        withAgentCatOnly.AddMcpServer().WithAgentCat("proj_test").WithAgentCat("proj_test");
        AssertOneAccessor(withAgentCatOnly);

        var appBeforeWithAgentCat = new ServiceCollection();
        appBeforeWithAgentCat.AddHttpContextAccessor();
        appBeforeWithAgentCat.AddMcpServer().WithAgentCat("proj_test");

        var appAfterWithAgentCat = new ServiceCollection();
        appAfterWithAgentCat.AddMcpServer().WithAgentCat("proj_test");
        appAfterWithAgentCat.AddHttpContextAccessor();

        AssertOneAccessor(appFirst);
        AssertOneAccessor(agentCatOnly);
        AssertOneAccessor(appBeforeWithAgentCat);
        AssertOneAccessor(appAfterWithAgentCat);
    }

    [Fact]
    public async Task Registration_happens_even_when_the_gate_is_off()
    {
        var log = new RecordingLog();
        var runtime = new AgentCatRuntime(log, new RecordingSender(), gate: new EnabledGate(static _ => "false", static () => false, log));
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton(runtime);
            services.AddMcpServer().WithAgentCat("proj_test");

            AssertOneAccessor(services);
            await using var provider = services.BuildServiceProvider();
            var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;

            Assert.False(EngineInstaller.IsInstalled(options));
            Assert.True(EnabledGate.SkippedAnInstall);
            Assert.IsType<HttpContextAccessor>(provider.GetService<IHttpContextAccessor>());
            Assert.Empty(log.Lines);
        }
        finally
        {
            runtime.Pipeline.Dispose();
        }
    }

    private static void AssertOneAccessor(IServiceCollection services)
    {
        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IHttpContextAccessor));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.Equal(typeof(HttpContextAccessor), descriptor.ImplementationType);
    }
}
#endif
