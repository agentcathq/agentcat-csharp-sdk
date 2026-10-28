using AgentCat.Internal.Delivery;

namespace AgentCat.Tests.Delivery;

public class ApiBaseUrlTests
{
    private static Func<string, string?> Env(string? agentcat = null, string? mcpcat = null)
        => name => name switch
        {
            "AGENTCAT_API_URL" => agentcat,
            "MCPCAT_API_URL" => mcpcat,
            _ => null,
        };

    [Fact]
    public void Should_call_configure_when_apiBaseUrl_option_is_provided()
        => Assert.Equal("https://opt.example.com", ApiBaseUrl.Resolve("https://opt.example.com", Env()));

    [Fact]
    public void Should_call_configure_with_MCPCAT_API_URL_env_var_when_no_option_is_set()
        => Assert.Equal("https://legacy.example.com", ApiBaseUrl.Resolve(null, Env(mcpcat: "https://legacy.example.com")));

    [Fact]
    public void Should_call_configure_with_AGENTCAT_API_URL_env_var_when_no_option_is_set()
        => Assert.Equal("https://new.example.com", ApiBaseUrl.Resolve(null, Env(agentcat: "https://new.example.com")));

    [Fact]
    public void Should_prioritize_AGENTCAT_API_URL_over_legacy_MCPCAT_API_URL()
        => Assert.Equal("https://new.example.com", ApiBaseUrl.Resolve(null, Env("https://new.example.com", "https://legacy.example.com")));

    [Fact]
    public void Should_fall_back_to_legacy_MCPCAT_API_URL_when_AGENTCAT_API_URL_is_unset()
        => Assert.Equal("https://legacy.example.com", ApiBaseUrl.Resolve(null, Env(null, "https://legacy.example.com")));

    [Fact]
    public void Should_prioritize_apiBaseUrl_option_over_MCPCAT_API_URL_env_var()
        => Assert.Equal("https://opt.example.com", ApiBaseUrl.Resolve("https://opt.example.com", Env(mcpcat: "https://legacy.example.com")));

    [Fact]
    public void Should_prioritize_apiBaseUrl_option_over_AGENTCAT_API_URL_env_var()
        => Assert.Equal("https://opt.example.com", ApiBaseUrl.Resolve("https://opt.example.com", Env(agentcat: "https://new.example.com")));

    [Fact]
    public void Should_not_call_configure_when_neither_option_nor_env_var_is_set()
        => Assert.Null(ApiBaseUrl.Resolve(null, Env()));

    [Fact]
    public void Empty_strings_count_as_unset()
    {
        Assert.Equal("https://new.example.com", ApiBaseUrl.Resolve("", Env("https://new.example.com", "https://legacy.example.com")));
        Assert.Equal("https://legacy.example.com", ApiBaseUrl.Resolve("", Env("", "https://legacy.example.com")));
        Assert.Null(ApiBaseUrl.Resolve("", Env("", "")));
    }

    [Fact]
    public void Should_reconfigure_the_apiClient_with_the_given_base_URL()
    {
        using var sender = new HttpEventSender();
        Assert.Equal("https://api.agentcat.com/v1/public/events/", sender.Endpoint);
        sender.SetBaseUrl("https://custom.example.com");
        Assert.Equal("https://custom.example.com/v1/public/events/", sender.Endpoint);
    }

    [Fact]
    public void Endpoint_keeps_a_path_prefix_and_drops_trailing_slashes()
        => Assert.Equal(
            "https://proxy.example.com/agentcat/v1/public/events/",
            ApiBaseUrl.EventsEndpoint("https://proxy.example.com/agentcat/"));
}
