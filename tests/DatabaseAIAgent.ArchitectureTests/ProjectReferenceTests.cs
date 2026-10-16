using System.Xml.Linq;

namespace DatabaseAIAgent.ArchitectureTests;

/// <summary>
/// Compiled-assembly checks only see references that are actually used. These tests also verify the declared
/// project references, so a forbidden dependency is caught before any code starts using it.
/// </summary>
public sealed class ProjectReferenceTests
{
    private static readonly Dictionary<string, string[]> AllowedReferences = new()
    {
        ["Domain"] = [],
        ["Application"] = ["Domain"],
        ["Agent"] = ["Application", "Domain"],
        ["Rag"] = ["Application", "Domain"],
        ["Tools"] = ["Application", "Domain"],
        ["Database"] = ["Application", "Domain"],
        ["Billing"] = ["Application", "Domain"],
        ["Security"] = ["Application", "Domain"],
        ["Infrastructure"] = ["Application", "Domain"],
    };

    public static TheoryData<string> LibraryNames => new(AllowedReferences.Keys);

    [Theory]
    [MemberData(nameof(LibraryNames))]
    public void LibraryProject_DeclaresOnlyAllowedProjectReferences(string project)
    {
        var declared = DeclaredProjectReferences(project);

        Assert.All(declared, reference => Assert.Contains(reference, AllowedReferences[project]));
    }

    private static string[] DeclaredProjectReferences(string project)
    {
        var path = Path.Combine(FindRepositoryRoot(), "src", $"DatabaseAIAgent.{project}", $"DatabaseAIAgent.{project}.csproj");
        return XDocument.Load(path)
            .Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value.Replace('\\', '/')))
            .Select(name => name["DatabaseAIAgent.".Length..])
            .ToArray();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DatabaseAIAgent.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root (DatabaseAIAgent.slnx) not found.");
    }
}
