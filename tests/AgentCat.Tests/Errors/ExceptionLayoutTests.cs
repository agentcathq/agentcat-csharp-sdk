using AgentCat.Internal.Errors;

namespace AgentCat.Tests.Errors;

public class ExceptionLayoutTests
{
    [Fact]
    public void Own_frames_are_found_in_rewritten_text_with_a_lambda_method_inner_section()
    {
        var compiled = System.Linq.Expressions.Expression
            .Lambda<Func<int>>(
                System.Linq.Expressions.Expression.Throw(
                    System.Linq.Expressions.Expression.Constant(new InvalidOperationException("boom")),
                    typeof(int)))
            .Compile();
        var aggregate = (AggregateException)Thrown(new AggregateException(Thrown(new ArgumentException("first")), Thrown(() => compiled())));
        var raw = aggregate.ToString();
#if NET
        Assert.Matches(@"lambda_method\d", raw);
#else
        // .NET Framework names the method of every compiled lambda lambda_method, without the number .NET adds.
        Assert.Contains("lambda_method(", raw, StringComparison.Ordinal);
#endif
        var rewritten = ExceptionCapture.StableStack(raw);
        Assert.DoesNotMatch(@"lambda_method\d", rewritten);

        var onRewritten = ExceptionLayout.OwnFrames(rewritten, aggregate);

        Assert.NotNull(onRewritten);
        Assert.Contains("(Inner Exception #1)", rewritten.Substring(onRewritten.Value.End), StringComparison.Ordinal);
    }

    private static Exception Thrown(Exception exception)
    {
        try
        {
            throw exception;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static Exception Thrown(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("The action did not throw.");
    }
}
