using CodexQuotaTaskbar.Host.Provider;

namespace CodexQuotaTaskbar.Host.Tests.Provider;

public sealed class CodexAppServerClientTests
{
    [Fact]
    public void Parses_concatenated_json_values_without_dropping_the_connection()
    {
        var messages = CodexAppServerClient.ParseMessages("{\"id\":1,\"result\":{}}{\"method\":\"thread/status/changed\",\"params\":{}}");

        Assert.Equal(2, messages.Count);
        Assert.Equal(1, messages[0].GetProperty("id").GetInt32());
        Assert.Equal("thread/status/changed", messages[1].GetProperty("method").GetString());
    }

    [Fact]
    public void Keeps_valid_messages_before_trailing_diagnostic_noise()
    {
        var messages = CodexAppServerClient.ParseMessages("{\"id\":2,\"result\":{}}diagnostic");

        Assert.Single(messages);
        Assert.Equal(2, messages[0].GetProperty("id").GetInt32());
    }
}
