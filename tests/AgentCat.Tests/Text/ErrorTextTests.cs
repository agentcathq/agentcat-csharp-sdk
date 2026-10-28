using AgentCat.Internal.Text;

namespace AgentCat.Tests.Text;

public class ErrorTextTests
{
    [Fact]
    public void Of_gives_the_type_name_and_the_message()
        => Assert.Equal("InvalidOperationException: boom", ErrorText.Of(new InvalidOperationException("boom")));

    [Fact]
    public void Of_gives_an_empty_message_when_the_Message_getter_throws()
        => Assert.Equal("ThrowingMessageException: ", ErrorText.Of(new ThrowingMessageException()));

    private sealed class ThrowingMessageException : Exception
    {
        public override string Message => throw new InvalidOperationException("no message");
    }
}
