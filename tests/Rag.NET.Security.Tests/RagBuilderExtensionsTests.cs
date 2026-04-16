using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DependencyInjection;
using Rag.NET.Security;
using Xunit;

namespace Rag.NET.Security.Tests;

public class RagBuilderExtensionsTests
{
    [Fact]
    public void UseAuditLog_WithoutAddRagNetFirst_ThrowsInvalidOperationException()
    {
        var services = new ServiceCollection();
        // Create a RagBuilder manually without calling AddRagNet (so no RetrievalPipelineBuilder registered)
        var builder = new RagBuilder(services);

        var ex = Assert.Throws<InvalidOperationException>(() => builder.UseAuditLog());
        Assert.Contains("AddRagNet", ex.Message, StringComparison.Ordinal);
    }
}
