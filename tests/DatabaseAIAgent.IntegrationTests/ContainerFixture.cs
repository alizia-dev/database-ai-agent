using DotNet.Testcontainers.Containers;

namespace DatabaseAIAgent.IntegrationTests;

/// <summary>
/// Starts a real database container once per test class. When Docker is not available the tests are skipped (not
/// failed), so the suite stays green on machines without Docker; CI with Docker runs them fully.
/// </summary>
public abstract class ContainerFixture<TContainer> : IAsyncLifetime
    where TContainer : IContainer
{
    private TContainer? _container;

    public string? SkipReason { get; private set; }

    public TContainer Container => _container ?? throw new InvalidOperationException("Container not started.");

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = Build();
            await _container.StartAsync();
            await SeedAsync();
        }
        catch (Exception ex) when (ex.GetType().Name.Contains("Docker", StringComparison.Ordinal) || ex is TimeoutException || ex.InnerException?.GetType().Name.Contains("Docker", StringComparison.Ordinal) == true)
        {
            SkipReason = $"Docker is not available ({ex.GetType().Name}); integration tests against real databases were skipped.";
        }
        catch (ArgumentException ex) when (ex.Message.Contains("Docker", StringComparison.OrdinalIgnoreCase))
        {
            SkipReason = $"Docker is not available ({ex.Message}).";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    public void SkipIfUnavailable()
    {
        if (SkipReason is not null)
        {
            Assert.Skip(SkipReason);
        }
    }

    protected abstract TContainer Build();

    /// <summary>Creates a sample schema, one stored procedure and a least-privilege account (as in the setup scripts).</summary>
    protected abstract Task SeedAsync();
}
