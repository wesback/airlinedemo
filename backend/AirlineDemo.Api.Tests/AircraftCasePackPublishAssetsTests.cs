using System.Xml.Linq;

namespace AirlineDemo.Api.Tests;

public sealed class AircraftCasePackPublishAssetsTests
{
    private const string CasePackGlob = "Fixtures/aircraft-case-pack/**/*";
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void CasePackFilesHaveExactPublishMetadata()
    {
        var project = XDocument.Load(Path.Combine(RepoRoot, "backend", "AirlineDemo.Api", "AirlineDemo.Api.csproj"));
        var item = project.Descendants().SingleOrDefault(element =>
            element.Name.LocalName == "None"
            && string.Equals((string?)element.Attribute("Include"), CasePackGlob, StringComparison.Ordinal));

        Assert.NotNull(item);
        Assert.Equal("PreserveNewest", Metadata(item!, "CopyToOutputDirectory"));
        Assert.Equal("PreserveNewest", Metadata(item!, "CopyToPublishDirectory"));
        Assert.Null(Metadata(item!, "Link"));
        Assert.Null(Metadata(item!, "LinkBase"));
        Assert.Null(Metadata(item!, "TargetPath"));
    }

    private static string? Metadata(XElement item, string name) =>
        item.Elements().FirstOrDefault(element => string.Equals(element.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value
        ?? (string?)item.Attributes().FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "backend", "AirlineDemo.Api", "AirlineDemo.Api.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"AirlineDemo.Api.csproj not found above {AppContext.BaseDirectory}.");
    }
}
