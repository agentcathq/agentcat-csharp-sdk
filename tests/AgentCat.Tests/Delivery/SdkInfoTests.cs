using AgentCat.Internal;

namespace AgentCat.Tests.Delivery;

public class SdkInfoTests
{
    [Fact]
    public void SdkInfo_user_agent_has_version_and_runtime()
    {
        Assert.Equal("C#", SdkInfo.Language);
        Assert.NotEqual("unknown", SdkInfo.Version);
        Assert.DoesNotContain("+", SdkInfo.Version);
        Assert.False(string.IsNullOrWhiteSpace(SdkInfo.RuntimeVersion));
        Assert.DoesNotContain(" ", SdkInfo.RuntimeVersion);
        Assert.Equal("agentcat-csharp/" + SdkInfo.Version + " dotnet/" + SdkInfo.RuntimeVersion, SdkInfo.UserAgent);
    }
}
