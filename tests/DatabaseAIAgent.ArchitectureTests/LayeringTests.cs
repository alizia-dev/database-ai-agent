using System.Reflection;

namespace DatabaseAIAgent.ArchitectureTests;

/// <summary>
/// Enforces the Clean Architecture dependency rules on compiled assemblies:
/// Domain depends on nothing; Application depends only on Domain; feature modules and Infrastructure depend
/// only on Application/Domain (never on each other); nothing depends on the host projects.
/// </summary>
public sealed class LayeringTests
{
    private const string Prefix = "DatabaseAIAgent.";

    private static readonly string[] FeatureModules =
        ["Agent", "Rag", "Tools", "Database", "Billing", "Security", "Infrastructure"];

    private static readonly string[] Hosts = ["Api", "Workers"];

    public static TheoryData<string> FeatureModuleNames => new(FeatureModules);

    [Fact]
    public void Domain_DependsOnNoOtherProjectAndNoFrameworkInfrastructure()
    {
        var references = ReferencedNames("Domain");

        Assert.DoesNotContain(references, r => r.StartsWith(Prefix, StringComparison.Ordinal));
        Assert.DoesNotContain(references, r => r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r => r.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    [Fact]
    public void Application_DependsOnlyOnDomain()
    {
        var projectReferences = ProjectReferences("Application");

        Assert.All(projectReferences, r => Assert.Equal(Prefix + "Domain", r));
        Assert.DoesNotContain(ReferencedNames("Application"), r => r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(FeatureModuleNames))]
    public void FeatureModules_DependOnlyOnApplicationAndDomain(string module)
    {
        var allowed = new[] { Prefix + "Application", Prefix + "Domain" };

        Assert.All(ProjectReferences(module), r => Assert.Contains(r, allowed));
    }

    [Theory]
    [MemberData(nameof(FeatureModuleNames))]
    public void NoLibraryDependsOnHostProjects(string module)
    {
        var hostNames = Hosts.Select(h => Prefix + h).ToArray();

        Assert.DoesNotContain(ProjectReferences(module), hostNames.Contains);
    }

    private static Assembly Load(string name) => Assembly.Load(Prefix + name);

    private static string[] ReferencedNames(string name) =>
        Load(name).GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToArray();

    private static string[] ProjectReferences(string name) =>
        ReferencedNames(name).Where(r => r.StartsWith(Prefix, StringComparison.Ordinal)).ToArray();
}
