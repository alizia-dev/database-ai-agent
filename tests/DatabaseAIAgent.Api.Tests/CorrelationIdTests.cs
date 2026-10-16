using DatabaseAIAgent.Api.Middleware;

namespace DatabaseAIAgent.Api.Tests;

public sealed class CorrelationIdTests
{
    [Theory]
    [InlineData("abc-123")]
    [InlineData("A.b_C-9")]
    public void Resolve_AcceptsSafeIds(string inbound) => Assert.Equal(inbound, CorrelationIdMiddleware.Resolve(inbound));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("line\nbreak")]
    [InlineData("<script>")]
    public void Resolve_ReplacesUnsafeOrMissingIds(string? inbound)
    {
        var resolved = CorrelationIdMiddleware.Resolve(inbound);

        Assert.NotEqual(inbound, resolved);
        Assert.Equal(32, resolved.Length);
    }

    [Fact]
    public void Resolve_ReplacesOverlongIds() =>
        Assert.Equal(32, CorrelationIdMiddleware.Resolve(new string('a', 65)).Length);
}
