using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Tests.Models;

public class CallRefusedExceptionTests
{
    /// <summary>
    /// A blown budget is the canonical deliberate refusal: wraps let it through by the marker type,
    /// and existing <c>catch (InvalidOperationException)</c> blocks keep catching it.
    /// </summary>
    [Fact]
    public void BudgetExceededException_IsACallRefusal_AndStillAnInvalidOperationException()
    {
        Exception ex = new BudgetExceededException(CostWindow.Day, limit: 1m, spend: 1m);

        Assert.IsAssignableFrom<CallRefusedException>(ex);
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
    }

    [Fact]
    public void CallRefusedException_KeepsMessageAndInner()
    {
        var inner = new IOException("x");

        var ex = new CallRefusedException("refused", inner);

        Assert.Equal("refused", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }
}
