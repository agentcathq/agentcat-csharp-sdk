namespace AgentCat.Tests.TestSupport;

// Tests that call a public static of AgentCatSdk share AgentCatRuntime.Shared, so they run alone.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedRuntime
{
    internal const string Name = "SharedRuntime";
}
